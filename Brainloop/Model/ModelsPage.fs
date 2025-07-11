namespace Brainloop.Model

open System
open FSharp.Data.Adaptive
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web
open Microsoft.JSInterop
open IcedTasks
open MudBlazor
open Fun.Result
open Fun.Blazor
open Brainloop.Db


[<Route "models">]
type ModelsPage(modelService: IModelService, snackbar: ISnackbar, dialog: IDialogService, JS: IJSRuntime, logger: ILogger<ModelsPage>) as this =
    inherit FunBlazorComponent()

    let isCreating = cval false
    let isSaving = cval false
    let query = cval ""
    let modelsRefresher = cval 0


    let addValidators (form: AdaptiveForm<Model, string>) =
        form
            .AddValidators((fun x -> x.Name), false, [ Validators.required "Name is required" ])
            .AddValidators((fun x -> x.Api), false, [ Validators.required "Api is required" ])

    let createModel () = task {
        isCreating.Publish(true)
        do! Async.Sleep 100
        do! JS.ScrollToElementTop("models-container", "model-new-form", smooth = true)
    }

    [<Parameter; SupplyParameterFromQuery(Name = "query")>]
    member _.Filter
        with get () = query.Value
        and set (x: string) = query.Publish x


    member _.UpsertModel(value: Model, isForCreating) = task {
        isSaving.Publish true

        try
            do! modelService.UpsertModel(value)
            transact (fun _ ->
                modelsRefresher.Value <- modelsRefresher.Value + 1
                isSaving.Value <- false
                if isForCreating then isCreating.Value <- false
            )
        with ex ->
            snackbar.ShowMessage(ex, logger)
            transact (fun _ -> isSaving.Value <- false)
    }


    member _.Header = fragment {
        PageTitle'' { "Models" }
        SectionContent'' {
            SectionName Constants.NavActionsSectionName
            MudSpacer''
        }
        MudText'' {
            Typo Typo.h3
            Align Align.Center
            style { margin 20 0 24 0 }
            "Models"
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
                        Placeholder "Search Models"
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
                    OnClick(ignore >> createModel)
                    "Create"
                }
            }
        }
    }


    member _.ModelForm(model: Model, isForCreating: bool) =
        let form = new AdaptiveForm<Model, string>(model) |> addValidators
        fragment {
            ModelCard.Create(form)
            div {
                style {
                    displayFlex
                    justifyContentFlexEnd
                    gap 12
                    paddingTop 12
                }
                region {
                    let modelId = form.GetFieldValue(fun x -> x.Id)
                    if modelId > 0 then
                        MudButton'' {
                            OnClick(fun _ -> task {
                                let! result = dialog.ShowMessageBox("Warning", "Are you sure to delete this model?")
                                if result.HasValue && result.Value then
                                    try
                                        do! modelService.DeleteModel(modelId)
                                        modelsRefresher.Publish((+) 1)
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
                            OnClick(fun _ -> if isForCreating then isCreating.Publish false else form.SetValue model)
                            "Cancel"
                        }
                        MudButton'' {
                            Variant Variant.Filled
                            Color Color.Primary
                            OnClick(fun _ -> this.UpsertModel(form.GetValue(), isForCreating))
                            Disabled(isSaving' || errors.Length > 0)
                            "Save"
                        }
                }
            }
        }


    member _.ModelsView = MudExpansionPanels'' {
        MultiExpansion
        adapt {
            let! _ = modelsRefresher
            let! models = modelService.GetModels() |> ValueTask.map (Seq.sortBy (fun x -> x.Name)) |> AVal.ofValueTask Seq.empty
            let! query = query

            let hasEmbedding = models |> Seq.exists (fun x -> x.CanHandleEmbedding)

            let filteredModels =
                models |> Seq.filter (fun x -> String.IsNullOrEmpty query || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))

            region {
                if not hasEmbedding && Seq.length models > 0 then
                    MudExpansionPanel'' {
                        Expanded true
                        TitleContent(
                            MudText'' {
                                Color Color.Warning
                                "For memory embedding"
                            }
                        )
                        this.ModelForm(
                            {
                                Model.Default with
                                    Name = "Memory Embedding"
                                    CanHandleEmbedding = true
                            },
                            false
                        )
                    }
            }
            for model in filteredModels do
                adaptiview (key = model.Id) {
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
                                model.Name
                                MudChipSet'' {
                                    Size Size.Small
                                    Color Color.Secondary
                                    if model.CanHandleFunctions then
                                        MudChip'' {
                                            Color Color.Info
                                            "Tools"
                                        }
                                    if model.CanHandleEmbedding then MudChip'' { "Embedding" }
                                }
                                if not model.CanHandleEmbedding then
                                    MudIconButton'' {
                                        Size Size.Small
                                        Icon Icons.Material.Outlined.CopyAll
                                        OnClick(fun _ ->
                                            this.UpsertModel(
                                                {
                                                    model with
                                                        Id = 0
                                                        Name = model.Name + " (Copy)"
                                                        CreatedAt = DateTime.Now
                                                },
                                                false
                                            )
                                        )
                                    }
                            }
                        )
                        region { if isExpanded then html.inject (model, fun () -> this.ModelForm(model, false)) }
                    }
                }
        }
    }


    override _.Render() = div {
        id "models-container"
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
                | true ->
                    MudPaper'' {
                        id "model-new-form"
                        style {
                            padding 12
                            displayFlex
                            flexDirectionColumn
                            gap 12
                        }
                        Elevation 2
                        this.ModelForm(Model.Default, true)
                    }
                    div {
                        style { padding 24 }
                        MudDivider''
                    }
                | _ -> ()
            }
            this.ModelsView
        }
    }
