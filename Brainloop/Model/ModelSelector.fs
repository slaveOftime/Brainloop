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
