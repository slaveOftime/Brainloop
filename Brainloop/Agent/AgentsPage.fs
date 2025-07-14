namespace Brainloop.Agent

open System
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web
open Microsoft.JSInterop
open MudBlazor
open Fun.Result
open Fun.Blazor
open Brainloop.Db


[<Route "agents">]
type AgentsPage(agentService: IAgentService, snackbar: ISnackbar, dialog: IDialogService, JS: IJSRuntime, logger: ILogger<AgentsPage>) as this =
    inherit FunBlazorComponent()

    let isCreating = cval false
    let isSaving = cval false
    let query = cval ""
    let agentsRefresher = cval 0

    let addValidators (form: AdaptiveForm<Agent, string>) =
        form
            .AddValidators((fun x -> x.Name), false, [ Validators.required "Name is required" ])
            .AddValidators((fun x -> x.AgentModels), false, [ Validators.seqMinLen 1 (fun _ -> "Add at least one model") ])

    let createAgent () = task {
        isCreating.Publish(true)
        do! Async.Sleep 100
        do! JS.ScrollToElementTop("agents-container", "agent-new-form", smooth = true)
    }


    [<Parameter; SupplyParameterFromQuery(Name = "query")>]
    member _.Filter
        with get () = query.Value
        and set (x: string) = query.Publish x


    member _.UpsertAgent(value: Agent, isForCreating: bool) = task {
        isSaving.Publish true
        try
            do! agentService.UpsertAgent(value)
            transact (fun _ ->
                agentsRefresher.Value <- agentsRefresher.Value + 1
                isSaving.Value <- false
                if isForCreating then isCreating.Value <- false
            )
        with ex ->
            snackbar.ShowMessage(ex, logger)
            transact (fun _ -> isSaving.Value <- false)
    }


    member _.Header = fragment {
        PageTitle'' { "Agents" }
        SectionContent'' {
            SectionName Strings.NavActionsSectionName
            MudSpacer''
        }
        MudText'' {
            Typo Typo.h3
            Align Align.Center
            style { margin 20 0 24 0 }
            "Agents"
        }
        div {
            style {
                displayFlex
                alignItemsCenter
                justifyContentCenter
                gap 24
                marginBottom 20
            }
            div {
                style { maxWidth 400 }
                adapt {
                    let! binding = query.WithSetter()
                    MudTextField'' {
                        Placeholder "Search agents"
                        Value' binding
                        Variant Variant.Outlined
                        FullWidth
                        Margin Margin.Dense
                        Adornment Adornment.End
                        AdornmentIcon Icons.Material.Filled.Search
                    }
                }
            }
            adapt {
                let! isCreating = isCreating
                MudButton'' {
                    Variant Variant.Filled
                    Color Color.Primary
                    Disabled isCreating
                    OnClick(ignore >> createAgent)
                    "Create"
                }
            }
        }
    }


    member _.AgentForm(agent: Agent, isForCreating: bool) =
        let form = new AdaptiveForm<Agent, string>(agent) |> addValidators
        fragment {
            AgentCard.Create(form)
            div {
                style {
                    displayFlex
                    justifyContentFlexEnd
                    gap 12
                    paddingTop 12
                }
                region {
                    let agentId = form.GetFieldValue(fun x -> x.Id)
                    if agentId > 0 then
                        MudButton'' {
                            OnClick(fun _ -> task {
                                let! result = dialog.ShowMessageBox("Warning", "Are you sure to delete this agent?")
                                if result.HasValue && result.Value then
                                    try
                                        do! agentService.DeleteAgent(agentId)
                                        agentsRefresher.Publish((+) 1)
                                    with ex ->
                                        snackbar.ShowMessage(ex, logger)
                            })
                            "Delete"
                        }
                }
                adapt {
                    let! isSaving' = isSaving
                    let! hasChanges = form.UseHasChanges()
                    let! errors = form.UseErrors()
                    if hasChanges then
                        MudButton'' {
                            Variant Variant.Text
                            OnClick(fun _ -> form.SetValue agent)
                            "Cancel"
                        }
                        MudButton'' {
                            Variant Variant.Filled
                            Color Color.Primary
                            OnClick(fun _ -> this.UpsertAgent(form.GetValue(), isForCreating))
                            Disabled(isSaving' || errors.Length > 0)
                            "Save"
                        }
                }
            }
        }

    member _.AgentsView = MudExpansionPanels'' {
        MultiExpansion
        adapt {
            let! _ = agentsRefresher
            let! agents = agentService.GetAgents().AsTask() |> Task.map (Seq.sortBy (fun x -> x.Name)) |> AVal.ofTask Seq.empty
            let! query = query

            let hasTitleBuilder = agents |> Seq.exists (fun x -> x.Type = AgentType.CreateTitle)
            let hasImageToTextBuilder = agents |> Seq.exists (fun x -> x.Type = AgentType.GetTextFromImage)

            let filteredAgents =
                agents
                |> Seq.filter (fun x ->
                    String.IsNullOrEmpty query
                    || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                )


            region {
                if not hasTitleBuilder && Seq.length agents > 0 then
                    MudExpansionPanel'' {
                        Expanded false
                        TitleContent(
                            MudText'' {
                                Color Color.Warning
                                "For automatically create title for your loop"
                            }
                        )
                        this.AgentForm(
                            {
                                Agent.Default with
                                    Name = "Create Title"
                                    Type = AgentType.CreateTitle
                                    Prompt = Prompts.CREATE_TITLE
                            },
                            false
                        )
                    }
            }
            region {
                if not hasImageToTextBuilder && Seq.length agents > 0 then
                    MudExpansionPanel'' {
                        Expanded false
                        TitleContent(
                            MudText'' {
                                Color Color.Warning
                                "For automatically get text from image"
                            }
                        )
                        this.AgentForm(
                            {
                                Agent.Default with
                                    Name = "Get text from image"
                                    Type = AgentType.GetTextFromImage
                                    Prompt = Prompts.GET_TEXT_FROM_IMAGE
                            },
                            false
                        )
                    }
            }
            for agent in filteredAgents do
                adaptiview (key = agent.Id) {
                    let! isExpanded, setIsExpanded = cval(false).WithSetter()
                    MudExpansionPanel'' {
                        Expanded isExpanded
                        ExpandedChanged setIsExpanded
                        TitleContent(
                            div {
                                style {
                                    displayFlex
                                    alignItemsCenter
                                    gap 12
                                }
                                agent.Name
                                MudChipSet'' {
                                    Size Size.Small
                                    MudChip'' {
                                        Color(if agent.Type = AgentType.CreateTitle then Color.Primary else Color.Default)
                                        string agent.Type
                                    }
                                    if agent.EnableTools then
                                        MudChip'' {
                                            Color Color.Info
                                            "Tools"
                                        }
                                }
                            }
                        )
                        region { if isExpanded then html.inject (agent, fun () -> this.AgentForm(agent, false)) }
                    }
                }
        }
    }


    override _.Render() = div {
        id "agents-container"
        style {
            zIndex 1
            height "100%"
            overflowYAuto
            backgroundColor "var(--mud-palette-background)"
        }
        MudContainer'' {
            MaxWidth MaxWidth.Medium
            this.Header
            adapt {
                match! isCreating with
                | false -> ()
                | true ->
                    MudPaper'' {
                        id "agent-new-form"
                        style {
                            padding 12
                            displayFlex
                            flexDirectionColumn
                            gap 12
                        }
                        Elevation 2
                        this.AgentForm(
                            {
                                Agent.Default with
                                    Type = AgentType.General
                                    Prompt = Prompts.GENERAL_ASSISTANT
                            },
                            true
                        )
                    }
                    div {
                        style { padding 24 }
                        MudDivider''
                    }
            }
            this.AgentsView
        }
    }
