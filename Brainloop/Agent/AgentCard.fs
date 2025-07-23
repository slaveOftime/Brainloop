namespace Brainloop.Agent

open System
open System.Linq
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Components.Web
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open BlazorMonaco
open BlazorMonaco.Editor
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Model
open Brainloop.Function


type AgentCard =

    static member Create(agentForm: AdaptiveForm<Agent, string>, ?groups: string seq) = MudGrid'' {
        Spacing 4
        MudItem'' {
            xs 12
            adapt {
                let! v, setV = agentForm.UseField(fun x -> x.Type)
                MudSelect'' {
                    Value v
                    ValueChanged(fun agentType ->
                        setV agentType

                        let agentPrompt = agentForm.GetFieldValue(fun x -> x.Prompt)
                        if String.IsNullOrWhiteSpace agentPrompt then
                            match agentType with
                            | AgentType.CreateTitle -> agentForm.UseFieldSetter (fun x -> x.Prompt) Prompts.CREATE_TITLE
                            | AgentType.GetTextFromImage -> agentForm.UseFieldSetter (fun x -> x.Prompt) Prompts.GET_TEXT_FROM_IMAGE
                            | AgentType.General -> agentForm.UseFieldSetter (fun x -> x.Prompt) Prompts.GENERAL_ASSISTANT
                    )
                    Label "Agent Type"
                    for option in [ AgentType.CreateTitle; AgentType.GetTextFromImage; AgentType.General ] do
                        MudSelectItem'' {
                            Value option
                            match option with
                            | AgentType.CreateTitle -> "Create Title"
                            | AgentType.GetTextFromImage -> "Get Text From Image"
                            | AgentType.General -> "General Assistant"
                        }
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = agentForm.UseFieldWithErrors(fun x -> x.Name)
                MudTextField'' {
                    Value' binding
                    Label "Name"
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = agentForm.UseField(fun x -> x.Description)
                MudTextField'' {
                    Value' binding
                    Label "Description"
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! v, setV = agentForm.UseField(fun x -> x.Group)
                MudAutocomplete'' {
                    Label "Group"
                    Value v
                    ValueChanged setV
                    MaxItems 200
                    CoerceValue
                    Clearable
                    OnClearButtonClick(fun _ -> setV null)
                    SearchFunc(fun q _ -> task {
                        let models = groups |> Option.defaultValue Seq.empty
                        return
                            seq {
                                match q with
                                | SafeString q -> yield! models |> Seq.filter (fun x -> x.Contains(q, StringComparison.OrdinalIgnoreCase))
                                | _ -> yield! models
                            }
                            |> Seq.distinct
                    })
                }
            }
        }
        MudItem'' {
            xs 4
            adapt {
                let! binding = agentForm.UseField(fun x -> x.Temperature)
                MudNumericField'' {
                    Label "Temperature"
                    Value' binding
                    Step 0.1
                    Min 0.1
                    Max 2.0
                }
            }
        }
        MudItem'' {
            xs 4
            adapt {
                let! binding = agentForm.UseField(fun x -> x.TopP)
                MudNumericField'' {
                    Label "Top-P"
                    Value' binding
                    Step 0.1
                    Min 0.1
                    Max 1.0
                }
            }
        }
        MudItem'' {
            xs 4
            adapt {
                let! binding = agentForm.UseField(fun x -> x.TopK)
                MudNumericField'' {
                    Label "Top-K"
                    Value' binding
                    Step 10
                    Min 1
                }
            }
        }
        MudItem'' {
            xs 12
            md 4
            adapt {
                let! binding = agentForm.UseField(fun x -> x.MaxHistory)
                MudNumericField'' {
                    Label "Max history items to include"
                    Value' binding
                }
            }
        }
        MudItem'' {
            xs 12
            md 4
            adapt {
                let! binding = agentForm.UseField(fun x -> x.MaxTimeoutMs)
                MudNumericField'' {
                    Label "Max timeout (ms)"
                    Value' binding
                }
            }
        }
        MudItem'' {
            xs 12
            ErrorBoundary'' {
                ErrorContent(fun error -> MudAlert'' {
                    Severity Severity.Error
                    error.ToString()
                })
                AgentCard.PromptEditor(agentForm.UseField(fun x -> x.Prompt))
            }
        }
        MudItem'' {
            xs 12
            MudField'' {
                Label "Models"
                Variant Variant.Outlined
                html.inject (fun (modelService: IModelService) -> adapt {
                    let! agentId = agentForm.UseFieldValue(fun x -> x.Id)
                    let! enableTools = agentForm.UseFieldValue(fun x -> x.EnableTools)
                    let! (agentModels, setAgentModels), errors = agentForm.UseFieldWithErrors(fun x -> x.AgentModels)

                    let agentModels = agentModels |> Seq.sortBy (fun x -> x.Order) |> Seq.toList

                    div {
                        MudAutocomplete'' {
                            Placeholder "Search to add model"
                            Margin Margin.Dense
                            Errors errors
                            FullWidth true
                            MaxItems 200
                            SearchFunc(fun q _ -> task {
                                let! models = modelService.GetModelsWithCache()
                                return
                                    match q with
                                    | SafeString q -> models |> Seq.filter (fun x -> x.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                    | _ -> models
                                    |> Seq.filter (fun x -> agentModels.Any(fun v -> v.ModelId = x.Id) |> not)
                                    |> Seq.filter (fun x -> not enableTools || x.CanHandleFunctions)
                                    |> Seq.filter (fun x -> not x.CanHandleEmbedding)
                                    |> Seq.map ValueSome
                            })
                            ToStringFunc(ValueOption.map (fun x -> x.Name) >> ValueOption.defaultValue "")
                            Value ValueNone
                            ValueChanged(
                                function
                                | ValueSome x when agentModels.Any(fun v -> v.ModelId = x.Id) |> not ->
                                    agentModels
                                    |> Seq.append [|
                                        {
                                            AgentModel.Create(agentId, x.Id) with
                                                Order = agentModels.Length
                                                Model = x
                                        }
                                    |]
                                    |> Seq.toArray
                                    |> setAgentModels
                                | _ -> ()
                            )
                        }
                        MudDropContainer''<AgentModel> {
                            key agentModels
                            Items(agentModels)
                            ItemsSelector(fun _ _ -> true)
                            ItemDropped(fun e ->
                                match e.Item with
                                | null -> ()
                                | item ->
                                    agentModels
                                    |> Seq.insertAt e.IndexInZone {
                                        Id = 0
                                        AgentId = agentId
                                        ModelId = item.ModelId
                                        Order = -1
                                        Model = item.Model
                                    }
                                    |> Seq.filter (fun x -> x.ModelId <> item.ModelId || (x.ModelId = item.ModelId && x.Order = -1)) // Remove the old one and keep the inserted one
                                    |> Seq.mapi (fun i x -> { x with Order = i })
                                    |> Seq.toArray
                                    |> setAgentModels
                            )
                            ItemRenderer(fun item -> MudPaper'' {
                                key (item.ModelId, item.Order)
                                style {
                                    margin 8 0
                                    paddingLeft 12
                                    displayFlex
                                    alignItemsCenter
                                    justifyContentSpaceBetween
                                }
                                item.Model.Name
                                MudIconButton'' {
                                    Icon Icons.Material.Filled.Delete
                                    OnClick(fun _ ->
                                        agentModels |> Seq.filter (fun x -> x.ModelId <> item.ModelId) |> Seq.toArray |> setAgentModels
                                    )
                                }
                            })
                            MudDropZone''<AgentModel> { AllowReorder }
                        }
                    }
                })
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = agentForm.UseField(fun x -> x.EnableStreaming)
                MudCheckBox'' {
                    Value' binding
                    Label "Enable Streaming"
                    Color Color.Primary
                }
            }
        }
        adapt {
            let! enableTools = agentForm.UseFieldValue(fun x -> x.EnableTools)
            let! enableStreaming = agentForm.UseFieldValue(fun x -> x.EnableStreaming)
            let! hasNoOpenAI =
                agentForm.UseFieldValue(fun x -> x.AgentModels)
                |> AVal.map (
                    Seq.exists (fun x ->
                        match x.Model.Provider with
                        | ModelProvider.OpenAI
                        | ModelProvider.OpenAIAzure _ -> false
                        | _ -> true
                    )
                )
            if enableTools && enableStreaming && hasNoOpenAI then
                MudItem'' {
                    xs 12
                    MudAlert'' {
                        Dense
                        Severity Severity.Warning
                        "None OpenAI model for tool invocation may not work when streaming is enabled."
                    }
                }
        }
        MudItem'' {
            xs 12
            md 4
            adapt {
                let! agentType = agentForm.UseFieldValue(fun x -> x.Type)
                match agentType with
                | AgentType.CreateTitle
                | AgentType.GetTextFromImage -> ()
                | AgentType.General ->
                    let! v, setV = agentForm.UseField(fun x -> x.EnableTools)
                    MudCheckBox'' {
                        Value v
                        ValueChanged(fun v ->
                            setV v
                            if v then
                                agentForm.UseFieldValue(fun x -> x.AgentModels).Value |> Seq.filter _.Model.CanHandleFunctions |> Seq.toArray
                                :> Collections.Generic.ICollection<AgentModel>
                                |> agentForm.UseFieldSetter(fun x -> x.AgentModels)
                        )
                        Label "Enable Tools"
                        Color Color.Primary
                    }
            }
        }
        adapt {
            match! agentForm.UseFieldValue(fun x -> x.EnableTools) with
            | false -> ()
            | true ->
                MudItem'' {
                    xs 12
                    md 4
                    adapt {
                        let! binding = agentForm.UseField(fun x -> x.EnableAgentCall)
                        MudCheckBox'' {
                            Value' binding
                            Label "Enable call agents"
                            Color Color.Primary
                        }
                    }
                }
                MudItem'' {
                    xs 12
                    md 4
                    adapt {
                        let! binding = agentForm.UseField(fun x -> x.EnableSelfCall)
                        MudCheckBox'' {
                            Value' binding
                            Label "Enable call self"
                            Color Color.Primary
                        }
                    }
                }
                MudItem'' {
                    xs 12
                    MudField'' {
                        Label "Toolset"
                        Variant Variant.Outlined
                        html.inject (fun (functionService: IFunctionService) -> adapt {
                            let! agentId = agentForm.UseFieldValue(fun x -> x.Id)
                            let! functions = functionService.GetFunctions() |> AVal.ofValueTask []
                            let! (agentFunctions, setAgentFunctions), errors = agentForm.UseFieldWithErrors(fun x -> x.AgentFunctions)
                            div {
                                MudAutocomplete'' {
                                    Placeholder "Search to add tools"
                                    Margin Margin.Dense
                                    Errors errors
                                    FullWidth true
                                    MaxItems 200
                                    SearchFunc(fun q _ -> task {
                                        return
                                            match q with
                                            | SafeString q ->
                                                functions |> Seq.filter (fun x -> x.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                                            | _ -> functions
                                            |> Seq.filter (fun x ->
                                                agentFunctions.Any(fun v ->
                                                    match v.Target with
                                                    | AgentFunctionTarget.Function id -> id = x.Id
                                                    | _ -> false
                                                )
                                                |> not
                                            )
                                            |> Seq.map ValueSome
                                    })
                                    ToStringFunc(ValueOption.map (fun x -> x.Name) >> ValueOption.defaultValue "")
                                    Value ValueNone
                                    ValueChanged(
                                        function
                                        | ValueSome x ->
                                            agentFunctions
                                            |> Seq.append [|
                                                {
                                                    AgentFunction.Id = 0
                                                    AgentId = agentId
                                                    Target = AgentFunctionTarget.Function x.Id
                                                    CreatedAt = DateTime.Now
                                                }
                                            |]
                                            |> Seq.toArray
                                            |> setAgentFunctions
                                        | _ -> ()
                                    )
                                }
                                MudChipSet''<AgentFunction> {
                                    Size Size.Small
                                    AllClosable
                                    OnClose(fun agentFunction ->
                                        agentFunctions
                                        |> Seq.filter (fun x -> x.Target <> agentFunction.Value.Target)
                                        |> Seq.toArray
                                        |> setAgentFunctions
                                    )
                                    for agentFunction in agentFunctions do
                                        functions
                                        |> Seq.tryFind (fun x ->
                                            match agentFunction.Target with
                                            | AgentFunctionTarget.Function id -> x.Id = id
                                            | _ -> false
                                        )
                                        |> Option.map (fun x -> MudChip'' {
                                            Value agentFunction
                                            x.Name
                                        })
                                        |> Option.defaultValue html.none
                                }
                            }
                        })
                    }
                }
                adapt {
                    match! agentForm.UseFieldValue(fun x -> x.EnableAgentCall) with
                    | false -> ()
                    | true -> MudItem'' {
                        xs 12
                        MudField'' {
                            Label "Agents for invoking"
                            Variant Variant.Outlined
                            html.inject (fun (agentService: IAgentService) -> adapt {
                                let! agentId = agentForm.UseFieldValue(fun x -> x.Id)
                                let! agents = agentService.GetAgentsWithCache() |> AVal.ofValueTask []
                                let! (agentFunctions, setAgentFunctions), errors = agentForm.UseFieldWithErrors(fun x -> x.AgentFunctions)
                                div {
                                    MudAutocomplete'' {
                                        Placeholder "Search to add agent"
                                        Margin Margin.Dense
                                        Errors errors
                                        FullWidth true
                                        MaxItems 200
                                        SearchFunc(fun q _ -> task {
                                            return
                                                match q with
                                                | SafeString q ->
                                                    agents
                                                    |> Seq.filter (fun x ->
                                                        x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                        || x.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                    )
                                                | _ -> agents
                                                |> Seq.filter (fun x ->
                                                    x.Id <> agentId
                                                    && agentFunctions.Any(fun v ->
                                                        match v.Target with
                                                        | AgentFunctionTarget.Agent id -> id = x.Id
                                                        | _ -> false
                                                       )
                                                       |> not
                                                )
                                                |> Seq.map ValueSome
                                        })
                                        ToStringFunc(ValueOption.map (fun x -> x.Name) >> ValueOption.defaultValue "")
                                        Value ValueNone
                                        ValueChanged(
                                            function
                                            | ValueSome x ->
                                                agentFunctions
                                                |> Seq.append [|
                                                    {
                                                        AgentFunction.Id = 0
                                                        AgentId = agentId
                                                        Target = AgentFunctionTarget.Agent x.Id
                                                        CreatedAt = DateTime.Now
                                                    }
                                                |]
                                                |> Seq.toArray
                                                |> setAgentFunctions
                                            | _ -> ()
                                        )
                                    }
                                    MudChipSet''<AgentFunction> {
                                        Size Size.Small
                                        AllClosable
                                        OnClose(fun agentFunction ->
                                            agentFunctions
                                            |> Seq.filter (fun x -> x.Target <> agentFunction.Value.Target)
                                            |> Seq.toArray
                                            |> setAgentFunctions
                                        )
                                        for agentFunction in agentFunctions do
                                            agents
                                            |> Seq.tryFind (fun x ->
                                                match agentFunction.Target with
                                                | AgentFunctionTarget.Agent id -> x.Id = id
                                                | _ -> false
                                            )
                                            |> Option.map (fun x -> MudChip'' {
                                                Value agentFunction
                                                x.Name
                                            })
                                            |> Option.defaultValue html.none
                                    }
                                }
                            })
                        }
                      }
                }
        }
        MudItem'' {
            xs 12
            adapt {
                let! v = agentForm.UseFieldValue(fun x -> x.LastUsedAt)
                if v.HasValue then
                    MudText'' {
                        Typo Typo.body2
                        "Last used at: "
                        v.Value.ToString()
                    }
            }
        }
    }


    static member Dialog(agentId: int, onClose) =
        html.inject (fun (agentService: IAgentService, loggerFactory: ILoggerFactory, snackbar: ISnackbar) -> task {
            let! agent = agentService.TryGetAgentWithCache(agentId)
            match agent with
            | ValueNone ->
                return MudDialog'' {
                    Header "Error" onClose
                    DialogContent(MudAlert'' { $"Agent is not found #{agentId}" })
                }
            | ValueSome agent ->
                let logger = loggerFactory.CreateLogger("AgentDialog")

                let agentForm = new AdaptiveForm<Agent, string>(agent)
                let isSaving = cval false

                return MudDialog'' {
                    Header
                        (MudText'' {
                            Typo Typo.subtitle1
                            Color Color.Primary
                            agent.Name
                        })
                        onClose
                    DialogContent(
                        div {
                            style {
                                maxHeight "calc(100vh - 200px)"
                                displayFlex
                                flexDirectionColumn
                            }
                            adapt {
                                match! isSaving with
                                | true -> MudProgressLinear'' {
                                    Color Color.Primary
                                    Indeterminate
                                  }
                                | _ -> ()
                            }
                            div {
                                style {
                                    overflowYAuto
                                    overflowXHidden
                                }
                                AgentCard.Create(agentForm)
                            }
                        }
                    )
                    DialogActions [|
                        adapt {
                            let! hasChanges = agentForm.UseHasChanges()
                            let! isSaving, setIsSaving = isSaving.WithSetter()
                            MudButton'' {
                                Color Color.Primary
                                Variant Variant.Filled
                                Disabled(not hasChanges || isSaving)
                                OnClick(fun _ -> task {
                                    setIsSaving true
                                    try
                                        do! agentService.UpsertAgent(agentForm.GetValue()) |> ValueTask.map ignore
                                        onClose ()
                                    with ex ->
                                        snackbar.ShowMessage(ex, logger)
                                    setIsSaving false
                                })
                                "Save"
                            }
                        }
                    |]
                }
        })


    static member private PromptEditor(prompt: aval<string * (string -> unit)>) : NodeRenderFragment =
        html.inject (
            prompt,
            fun (hook: IComponentHook, shareStore: IShareStore) ->
                let mutable hasChanges = false
                let mutable inputRef: StandaloneCodeEditor | null = null

                hook.RegisterAutoCompleteForAddAgents((fun _ -> inputRef), (ignore >> ValueTask.singleton))

                adapt {
                    let! isDarkMode = shareStore.IsDarkMode
                    let! prompt, setPrompt = prompt
                    MudField'' {
                        Label "Prompt"
                        Variant Variant.Outlined
                        div {
                            class' "agent-prompt-editor"
                            StandaloneCodeEditor'' {
                                key prompt
                                ConstructionOptions(fun _ ->
                                    StandaloneEditorConstructionOptions(
                                        Value = prompt,
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
                                OnDidChangeModelContent(fun _ -> hasChanges <- true)
                                OnDidBlurEditorWidget(fun _ -> task {
                                    if hasChanges then
                                        match inputRef with
                                        | null -> ()
                                        | ref ->
                                            let! text = ref.GetValue()
                                            setPrompt text
                                })
                                ref (fun x -> inputRef <- x)
                            }
                        }
                        styleElt { ruleset ".agent-prompt-editor .monaco-editor-container" { height "300px" } }
                    }
                }
        )
