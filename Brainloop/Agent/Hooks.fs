[<AutoOpen>]
module Brainloop.Agent.Hooks

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.JSInterop
open Fun.Blazor
open BlazorMonaco.Editor
open BlazorMonaco.Languages
open Brainloop.Db


type IComponentHook with

    member hook.RegisterAutoCompleteForAddAgents
        (getInputRef: unit -> (StandaloneCodeEditor | null), onAgentSelected: Agent option -> ValueTask<unit>)
        =
        hook.AddFirstAfterRenderTask(fun _ -> task {
            let js = hook.ServiceProvider.GetRequiredService<IJSRuntime>()
            let agentService = hook.ServiceProvider.GetRequiredService<IAgentService>()
            let agentProviderId = Random.Shared.Next().ToString()

            do!
                js.RegisterCompletionItemProvider(
                    agentProviderId,
                    "markdown",
                    CompletionItemProvider(
                        System.Collections.Generic.List [ "@" ],
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
                            | _ -> return CompletionList()
                        }),
                        CompletionItemProvider.ResolveDelegate(fun item -> task {
                            let! agents = agentService.GetAgentsWithCache()
                            do! agents |> Seq.tryFind (fun x -> x.Name = item.LabelAsString) |> onAgentSelected
                            return item
                        })
                    )
                )

            hook.OnDispose.Add(fun _ -> js.UnregisterCompletionItemProvider(agentProviderId) |> ignore)
        })
