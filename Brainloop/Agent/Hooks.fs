[<AutoOpen>]
module Brainloop.Agent.Hooks

open System
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open Microsoft.JSInterop
open IcedTasks
open Fun.Blazor
open BlazorMonaco.Editor
open BlazorMonaco.Languages
open Brainloop.Db


type IComponentHook with

    member hook.RegisterAutoCompleteForAddAgents
        (getInputRef: unit -> (StandaloneCodeEditor | null), onAgentSelected: Agent option -> ValueTask<unit>, ?getAgentId: unit -> int voption)
        =
        hook.AddFirstAfterRenderTask(fun _ -> task {
            let js = hook.ServiceProvider.GetRequiredService<IJSRuntime>()
            let agentService = hook.ServiceProvider.GetRequiredService<IAgentService>()
            let agentProviderId = Random.Shared.Next().ToString()
            let plugins = Dictionary<int, KernelPlugin list>()

            do!
                js.RegisterCompletionItemProvider(
                    agentProviderId,
                    "markdown",
                    CompletionItemProvider(
                        System.Collections.Generic.List [
                            "@"
                            if getAgentId.IsSome then ">"
                        ],
                        CompletionItemProvider.ProvideDelegate(fun modelUri _ context -> task {
                            let! model = BlazorMonaco.Editor.Global.GetModel(js, modelUri)
                            match context.TriggerCharacter, getInputRef () with
                            | _, null -> return CompletionList()
                            | "@", editor ->
                                let! editorModel = editor.GetModel()
                                if model.Id <> editorModel.Id then
                                    return CompletionList()
                                else
                                    let! agents = agentService.GetAgentsWithCache()
                                    return
                                        CompletionList(
                                            Suggestions =
                                                System.Collections.Generic.List [
                                                    for agent in agents do
                                                        CompletionItem(
                                                            LabelAsString = agent.Name,
                                                            Kind = CompletionItemKind.User,
                                                            Command = Command(),
                                                            InsertText = if agent.Name.Contains(' ') then $"\"{agent.Name}\"" else agent.Name
                                                        )
                                                ]
                                        )
                            | ">", editor ->
                                match getAgentId with
                                | None -> return CompletionList()
                                | Some getAgentId ->
                                    let! editorModel = editor.GetModel()
                                    if model.Id <> editorModel.Id then
                                        return CompletionList()
                                    else
                                        let! plugins = valueTask {
                                            match getAgentId () with
                                            | ValueSome agentId ->
                                                match plugins.TryGetValue agentId with
                                                | true, x when not x.IsEmpty -> return x
                                                | _ ->
                                                    let! ps = agentService.GetKernelPlugins(agentId)
                                                    plugins[agentId] <- ps
                                                    return ps
                                            | _ -> return []
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
                        }),
                        CompletionItemProvider.ResolveDelegate(fun item -> task {
                            let! agents = agentService.GetAgentsWithCache()
                            match agents |> Seq.tryFind (fun x -> x.Name = item.LabelAsString) with
                            | None -> ()
                            | x -> do! onAgentSelected x
                            return item
                        })
                    )
                )

            hook.OnDispose.Add(fun _ -> js.UnregisterCompletionItemProvider(agentProviderId) |> ignore)
        })
