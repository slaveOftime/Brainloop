namespace Brainloop.Agent

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.JSInterop
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Agent


type AgentSelector =

    static member Create(selectedAgent: ValueOption<Agent> cval, selectedModel: ValueOption<Model> cval) =
        html.inject (
            "agents-selector",
            fun (serviceProvider: IServiceProvider, JS: IJSRuntime) ->
                let agentService = serviceProvider.GetRequiredService<IAgentService>()

                let mutable menuRef: MudMenu | null = null

                let agents = clist<Agent> ()
                let agentsFilter = cval ""


                let selectAgent (agent: Agent voption) = task {
                    selectedModel.Publish ValueNone
                    selectedAgent.Publish agent
                    match menuRef with
                    | null -> ()
                    | ref -> do! ref.CloseMenuAsync()
                }

                let openAgentsSelector () = task {
                    let! results = agentService.GetAgentsWithCache()
                    transact (fun _ ->
                        agents.Clear()
                        agents.AddRange results
                    )
                }

                let filterAgents (agentsFilter: string) (agents: Agent seq) =
                    if String.IsNullOrEmpty agentsFilter then
                        agents
                    else
                        agents
                        |> Seq.filter (fun x ->
                            x.Name.Contains(agentsFilter, StringComparison.OrdinalIgnoreCase)
                            || x.Description.Contains(agentsFilter, StringComparison.OrdinalIgnoreCase)
                            || (agentsFilter.StartsWith("to", StringComparison.OrdinalIgnoreCase) && x.EnableTools)
                            || x.AgentModels
                               |> Seq.exists (fun m ->
                                   m.Model.Name.Contains(agentsFilter, StringComparison.OrdinalIgnoreCase)
                                   || m.Model.Model.Contains(agentsFilter, StringComparison.OrdinalIgnoreCase)
                                   || m.Model.Provider.ToString().Contains(agentsFilter, StringComparison.OrdinalIgnoreCase)
                               )
                        )

                div {
                    style {
                        displayFlex
                        alignItemsCenter
                    }
                    adapt {
                        let! selectedAgent, setSelectedAgent = selectedAgent.WithSetter()
                        let! selectedModel, setSelectedModel = selectedModel.WithSetter()
                        MudMenu'' {
                            AnchorOrigin Origin.TopLeft
                            TransformOrigin Origin.BottomLeft
                            ActivatorContent(
                                match selectedAgent with
                                | ValueNone -> MudIconButton'' {
                                    OnClick(ignore >> openAgentsSelector)
                                    Icon Icons.Material.Filled.AlternateEmail
                                  }
                                | ValueSome agent -> MudButton'' {
                                    OnClick(ignore >> openAgentsSelector)
                                    Color Color.Primary
                                    Variant Variant.Text
                                    StartIcon Icons.Material.Filled.AlternateEmail
                                    agent.Name
                                    match selectedModel with
                                    | ValueNone -> ()
                                    | ValueSome model ->
                                        " - "
                                        model.Name
                                  }
                            )
                            ref (fun x -> menuRef <- x)
                            div {
                                class' "highlight-fst-menu-item"
                                style {
                                    minWidth 250
                                    maxWidth 400
                                    maxHeight 500
                                    overflowYAuto
                                }
                                adapt {
                                    let! agents = agents
                                    let! agentsFilter = agentsFilter
                                    let gropedAgents = filterAgents agentsFilter agents |> Seq.groupBy _.Group
                                    for g, agents in gropedAgents do
                                        match g with
                                        | NullOrEmptyString -> ()
                                        | SafeString g ->
                                            MudDivider''
                                            div {
                                                style {
                                                    displayFlex
                                                    alignItemsCenter
                                                    justifyContentCenter
                                                    gap 8
                                                    marginTop 4
                                                    marginBottom 4
                                                    opacity 0.75
                                                }
                                                MudText'' {
                                                    Typo Typo.body2
                                                    g
                                                }
                                                MudIcon'' {
                                                    Size Size.Small
                                                    Icon Icons.Material.Filled.KeyboardArrowDown
                                                }
                                            }
                                        for agent in agents do
                                            MudMenuItem'' {
                                                key agent.Id
                                                OnClick(fun _ -> selectAgent (ValueSome agent))
                                                MudText'' { agent.Name }
                                                MudText'' {
                                                    Typo Typo.body2
                                                    agent.Description
                                                }
                                                MudChipSet'' {
                                                    if agent.EnableTools then
                                                        MudChip'' {
                                                            Size Size.Small
                                                            Color Color.Info
                                                            "Tools"
                                                        }
                                                    for model in agent.AgentModels |> Seq.sortBy (fun x -> x.Order) do
                                                        MudChip'' {
                                                            stopPropagation "onclick" true
                                                            Size Size.Small
                                                            OnClick(fun _ -> task {
                                                                do! selectAgent (ValueSome agent)
                                                                setSelectedModel (ValueSome model.Model)
                                                            })
                                                            model.Model.Name
                                                        }
                                                }
                                            }
                                }
                            }
                            MudDivider''
                            div {
                                style { padding 8 }
                                adapt {
                                    let! v, setV = agentsFilter.WithSetter()
                                    MudTextField'' {
                                        Value v
                                        ValueChanged setV
                                        Placeholder "Filter agents"
                                        AutoFocus
                                        DebounceInterval 400
                                        OnKeyUp(fun e -> task {
                                            if e.Key = "Enter" then
                                                do! agents.Value |> filterAgents v |> Seq.tryHead |> ValueOption.ofOption |> selectAgent
                                        })
                                    }
                                }
                            }
                        }
                        match selectedAgent with
                        | ValueSome _ -> MudIconButton'' {
                            Size Size.Small
                            Variant Variant.Text
                            Icon Icons.Material.Filled.Close
                            OnClick(fun _ ->
                                setSelectedAgent ValueNone
                                setSelectedModel ValueNone
                            )
                          }
                        | _ -> ()
                    }
                }
        )
