namespace Brainloop.Model

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open FSharp.Control
open FSharp.Data.Adaptive
open MudBlazor
open Fun.Result
open Fun.Blazor
open Brainloop.Db


type ModelSelector =

    static member Create(form: AdaptiveForm<Model, string>) =
        html.inject (
            form,
            fun (snackbar: ISnackbar, modelService: IModelService, loggerFactory: ILoggerFactory) ->
                let logger = loggerFactory.CreateLogger("ModelSelector")
                adapt {
                    let! (model, onModelChanged), errors = form.UseFieldWithErrors(fun x -> x.Model)
                    MudAutocomplete'' {
                        Label "Model"
                        Value { Model = model; DisplayName = model }
                        ValueChanged(fun x ->
                            onModelChanged x.Model
                            if form.GetFieldValue(fun x -> x.Name) |> String.IsNullOrEmpty then
                                form.UseFieldSetter (fun x -> x.Name) x.Model
                        )
                        Errors errors
                        Immediate false
                        ShowProgressIndicator
                        MaxItems 200
                        ToStringFunc(fun x -> x.DisplayName)
                        SearchFunc(fun q ct -> task {
                            let userInputModel = { Model = q; DisplayName = q }
                            try
                                let! models = modelService.GetModelsFromSourceWithCache(form.GetValue(), cancellationToken = ct)
                                return
                                    match q with
                                    | SafeString q ->
                                        models
                                        |> Seq.filter (fun x ->
                                            x.Model.Contains(q, StringComparison.OrdinalIgnoreCase)
                                            || x.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                        )
                                    | _ -> models
                                    |> Seq.insertAt 0 userInputModel
                            with
                            | :? TaskCanceledException -> return Seq.empty
                            | ex ->
                                snackbar.ShowMessage(ex, logger)
                                return [ userInputModel ]
                        })
                    }
                }
        )


    static member Create(binding: aval<int * (int -> unit)>, ?required, ?filter: Model -> bool) =
        html.inject (
            binding,
            fun (modelService: IModelService) -> adapt {
                let isRequired = defaultArg required false
                let filter = filter |> Option.defaultValue (fun _ -> true)
                let! models = modelService.GetModelsWithCache() |> AVal.ofValueTask []
                let! model, onModelChanged = binding
                let model = models |> Seq.tryFind (fun x -> x.Id = model) |> Option.toValueOption
                MudAutocomplete'' {
                    Placeholder "Search to add model"
                    Margin Margin.Dense
                    FullWidth true
                    MaxItems 200
                    SearchFunc(fun q _ -> task {
                        return
                            match q with
                            | SafeString q -> models |> Seq.filter (fun x -> filter x && x.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                            | _ -> models
                            |> Seq.map ValueSome
                    })
                    ToStringFunc(ValueOption.map (fun x -> x.Name) >> ValueOption.defaultValue "")
                    Value model
                    ValueChanged(
                        function
                        | ValueSome x -> onModelChanged x.Id
                        | _ -> ()
                    )
                    Error(isRequired && model.IsNone)
                    ErrorText "Model is required"
                }
            }
        )



    static member CreateMenu(selectedModel: ValueOption<Model> cval) =
        html.inject (
            "models-selector",
            fun (modelService: IModelService) ->
                let mutable menuRef: MudMenu | null = null

                let models = clist<Model> ()
                let modelsFilter = cval ""


                let selectModel (model: Model voption) = task {
                    selectedModel.Publish model
                    match menuRef with
                    | null -> ()
                    | ref -> do! ref.CloseMenuAsync()
                }

                let openModelsSelector () = task {
                    let! results = modelService.GetModelsWithCache()
                    transact (fun _ ->
                        models.Clear()
                        models.AddRange results
                        modelsFilter.Value <-
                            match modelsFilter.Value, selectedModel.Value with
                            | NullOrEmptyString, ValueSome { Name = name } -> name
                            | x, _ -> x
                    )
                }

                let filterModels (modelsFilter: string) (models: Model seq) =
                    if String.IsNullOrEmpty modelsFilter then
                        models
                    else
                        models
                        |> Seq.filter (fun x ->
                            x.Name.Contains(modelsFilter, StringComparison.OrdinalIgnoreCase)
                            || x.Model.Contains(modelsFilter, StringComparison.OrdinalIgnoreCase)
                            || (
                                match x.Group with
                                | null -> false
                                | g -> g.Contains(modelsFilter, StringComparison.OrdinalIgnoreCase)
                            )
                        )

                adapt {
                    let! selectedModel, setSelectedModel = selectedModel.WithSetter()
                    MudButtonGroup'' {
                        Size Size.Small
                        Variant(if selectedModel.IsSome then Variant.Outlined else Variant.Text)
                        MudMenu'' {
                            AnchorOrigin Origin.TopLeft
                            TransformOrigin Origin.BottomLeft
                            ActivatorContent(
                                match selectedModel with
                                | ValueNone -> MudIconButton'' {
                                    OnClick(ignore >> openModelsSelector)
                                    Icon Icons.Material.Filled.Grain
                                  }
                                | ValueSome model -> MudButton'' {
                                    OnClick(ignore >> openModelsSelector)
                                    Color Color.Primary
                                    Variant Variant.Text
                                    StartIcon Icons.Material.Filled.Grain
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
                                    let! models = models
                                    let! modelsFilter = modelsFilter
                                    let gropedModels = filterModels modelsFilter models |> Seq.groupBy _.Group |> Seq.sortBy fst
                                    for g, models in gropedModels do
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
                                        for model in models do
                                            MudMenuItem'' {
                                                key model.Id
                                                OnClick(fun _ -> selectModel (ValueSome model))
                                                MudText'' { model.Name }
                                                if model.Name.Equals(model.Model, StringComparison.OrdinalIgnoreCase) |> not then
                                                    MudText'' {
                                                        Typo Typo.body2
                                                        model.Model
                                                    }
                                            }
                                }
                            }
                            MudDivider''
                            div {
                                style { padding 8 }
                                adapt {
                                    let! v, setV = modelsFilter.WithSetter()
                                    MudTextField'' {
                                        Value v
                                        ValueChanged setV
                                        Placeholder "Filter models"
                                        AutoFocus
                                        DebounceInterval 400
                                        OnKeyUp(fun e -> task {
                                            if e.Key = "Enter" then
                                                do! models.Value |> filterModels v |> Seq.tryHead |> ValueOption.ofOption |> selectModel
                                        })
                                    }
                                }
                            }
                        }
                        match selectedModel with
                        | ValueSome _ -> MudIconButton'' {
                            Size Size.Small
                            Variant Variant.Text
                            Icon Icons.Material.Filled.Close
                            OnClick(fun _ -> setSelectedModel ValueNone)
                          }
                        | _ -> ()
                    }
                }
        )
