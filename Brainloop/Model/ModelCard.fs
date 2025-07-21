namespace Brainloop.Model

open System
open Microsoft.Extensions.Logging
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open Fun.Blazor
open Fun.Result
open Brainloop.Db


type ModelCard =

    static member Create(modelForm: AdaptiveForm<Model, string>, ?groups: string seq) = MudGrid'' {
        Spacing 4
        adapt {
            let! (v, setV), errors = modelForm.UseFieldWithErrors(fun x -> x.Provider)
            MudItem'' {
                xs 12
                MudSelect'' {
                    Value v
                    ValueChanged(fun v ->
                        setV v
                        if modelForm.GetFieldValue(fun x -> x.Api) |> String.IsNullOrEmpty then
                            modelForm.UseFieldSetter (fun x -> x.Api) "http://localhost:11434"
                    )
                    Errors errors
                    Label "Model Provider"
                    for option in
                        [
                            ModelProvider.OpenAI
                            ModelProvider.OpenAIAzure OpenAIAzure.Default
                            ModelProvider.Ollama
                            ModelProvider.Google
                            ModelProvider.HuggingFace
                            ModelProvider.MistralAI
                        ] do
                        MudSelectItem'' {
                            Value option
                            string option
                        }
                }
            }
            region {
                match v with
                | ModelProvider.OpenAIAzure config -> MudItem'' {
                    xs 12
                    MudTextField'' {
                        Label "Deployment Id"
                        Value config.DepoymentId
                        ValueChanged(fun x -> setV (ModelProvider.OpenAIAzure { config with DepoymentId = x }))
                    }
                  }
                | _ -> ()
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = modelForm.UseFieldWithErrors(fun x -> x.Api)
                MudTextField'' {
                    Value' binding
                    Label "Api"
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! showPassword, setShowPassword = cval(false).WithSetter()
                let! binding = modelForm.UseFieldWithErrors(fun x -> x.ApiKey)
                MudTextField'' {
                    InputType(if showPassword then InputType.Text else InputType.Password)
                    Value' binding
                    Label "ApiKey"
                    Adornment Adornment.End
                    AdornmentIcon(
                        if showPassword then
                            Icons.Material.Outlined.Visibility
                        else
                            Icons.Material.Outlined.VisibilityOff
                    )
                    OnAdornmentClick(fun _ -> setShowPassword (not showPassword))
                }
            }
        }
        MudItem'' {
            xs 12
            MudField'' {
                Label "Api Headers"
                Variant Variant.Outlined
                adapt {
                    let! apiProps, setApiProps = modelForm.UseField(fun x -> x.ApiProps)
                    let apiProps =
                        match apiProps with
                        | ValueNone -> ModelApiProps.Default
                        | ValueSome x -> x
                    KeyValueField.Create(
                        apiProps.Headers,
                        (fun x -> setApiProps (ValueSome { apiProps with Headers = x })),
                        createNewPlacehold = "Input new header and press enter"
                    )
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = modelForm.UseFieldWithErrors(fun x -> x.Proxy)
                MudTextField'' {
                    Value' binding
                    Label "Proxy"
                }
            }
        }
        MudItem'' {
            xs 12
            ModelSelector.Create(modelForm)
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = modelForm.UseFieldWithErrors(fun x -> x.Name)
                MudTextField'' {
                    Value' binding
                    Label "Name"
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! v, setV = modelForm.UseField(fun x -> x.Group)
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
            xs 12
            div {
                style {
                    displayFlex
                    alignItemsCenter
                    flexWrapWrap
                    gap 12
                }
                adapt {
                    let! canHandleEmbedding, setCanHandleEmbedding = modelForm.UseField(fun x -> x.CanHandleEmbedding)
                    MudSwitch'' {
                        Value canHandleEmbedding
                        ValueChanged setCanHandleEmbedding
                        Label "Embedding"
                        Color Color.Primary
                    }

                    if canHandleEmbedding then
                        let! embeddingDimensions = modelForm.UseField(fun x -> x.EmbeddingDimensions)
                        div {
                            style { width 160 }
                            MudNumericField'' {
                                Value' embeddingDimensions
                                Margin Margin.Dense
                                Variant Variant.Outlined
                                Label "Dimensions"
                            }
                        }
                }
            }
        }
        adapt {
            let! canHandleEmbedding = modelForm.UseFieldValue(fun x -> x.CanHandleEmbedding)
            if not canHandleEmbedding then
                MudItem'' {
                    xs 12
                    MudField'' {
                        Label "Capabilities"
                        div {
                            style {
                                displayFlex
                                alignItemsCenter
                                flexWrapWrap
                                gap 12
                            }
                            adapt {
                                let! binding = modelForm.UseField(fun x -> x.CanHandleText)
                                MudCheckBox'' {
                                    Value' binding
                                    Label "Text"
                                    Color Color.Primary
                                }
                            }
                            adapt {
                                let! binding = modelForm.UseField(fun x -> x.CanHandleImage)
                                MudCheckBox'' {
                                    Value' binding
                                    Label "Image"
                                    Color Color.Primary
                                }
                            }
                            adapt {
                                let! binding = modelForm.UseField(fun x -> x.CanHandleVideo)
                                MudCheckBox'' {
                                    Value' binding
                                    Label "Video"
                                    Color Color.Primary
                                }
                            }
                            adapt {
                                let! binding = modelForm.UseField(fun x -> x.CanHandleAudio)
                                MudCheckBox'' {
                                    Value' binding
                                    Label "Audio"
                                    Color Color.Primary
                                }
                            }
                            adapt {
                                let! binding = modelForm.UseField(fun x -> x.CanHandleFunctions)
                                MudCheckBox'' {
                                    Value' binding
                                    Label "Tools"
                                    Color Color.Primary
                                }
                            }
                        }
                    }
                }
        }
        MudItem'' {
            xs 12
            adapt {
                let! v = modelForm.UseFieldValue(fun x -> x.LastUsedAt)
                if v.HasValue then
                    MudText'' {
                        Typo Typo.body2
                        "Last used at: "
                        v.Value.ToString()
                    }

                let! inputTokens = modelForm.UseFieldValue(fun x -> x.InputTokens) |> AVal.map (ValueOption.ofNullable >> ValueOption.defaultValue 0)
                let! outputTokens =
                    modelForm.UseFieldValue(fun x -> x.OutputTokens) |> AVal.map (ValueOption.ofNullable >> ValueOption.defaultValue 0)
                if inputTokens > 0 || outputTokens > 0 then
                    MudChip'' {
                        Size Size.Small
                        if inputTokens > 0 then
                            "↑"
                            int inputTokens
                            " "
                        if outputTokens > 0 then
                            "↓"
                            int outputTokens
                        " tokens"
                    }
            }
        }
    }


    static member Dialog(modelId: int, onClose) =
        html.inject (fun (modelService: IModelService, loggerFactory: ILoggerFactory, snackbar: ISnackbar) -> task {
            let! model = modelService.TryGetModelWithCache(modelId)
            match model with
            | ValueNone ->
                return MudDialog'' {
                    Header "Error" onClose
                    DialogContent(MudAlert'' { $"Model is not found #{modelId}" })
                }
            | ValueSome model ->
                let logger = loggerFactory.CreateLogger("AgentDialog")

                let modelForm = new AdaptiveForm<Model, string>(model)
                let isSaving = cval false

                return MudDialog'' {
                    Header
                        (MudText'' {
                            Typo Typo.subtitle1
                            Color Color.Primary
                            model.Name
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
                                ModelCard.Create(modelForm)
                            }
                        }
                    )
                    DialogActions [|
                        adapt {
                            let! hasChanges = modelForm.UseHasChanges()
                            let! isSaving, setIsSaving = isSaving.WithSetter()
                            MudButton'' {
                                Color Color.Primary
                                Variant Variant.Filled
                                Disabled(not hasChanges || isSaving)
                                OnClick(fun _ -> task {
                                    setIsSaving true
                                    try
                                        do! modelService.UpsertModel(modelForm.GetValue()) |> ValueTask.map ignore
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
