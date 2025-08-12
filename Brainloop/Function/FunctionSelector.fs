namespace Brainloop.Function

open System
open Microsoft.SemanticKernel
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open Fun.Blazor
open Brainloop.Db


type FunctionSelector =

    static member Create(selectedFunctions: KernelFunction clist, ?isLoading: bool cval) =
        html.inject (
            "functions-selector",
            fun (functionService: IFunctionService, dialogService: IDialogService) ->
                let mutable menuRef: MudMenu | null = null
                let mutable firstMatchFunction: KernelFunction | null = null

                let functions = clist<KernelPlugin> ()
                let functionsFilter = cval ""

                let isLoading =
                    match isLoading with
                    | Some x -> x
                    | None -> cval false


                let selectFunction (fn: KernelFunction) = task {
                    transact (fun _ ->
                        if selectedFunctions |> Seq.exists ((=) fn) |> not then
                            selectedFunctions.Add fn |> ignore
                    )
                    match menuRef with
                    | null -> ()
                    | ref -> do! ref.CloseMenuAsync()
                }

                let clearSelections () = task {
                    transact (fun _ -> selectedFunctions.Clear() |> ignore)
                    match menuRef with
                    | null -> ()
                    | ref -> do! ref.CloseMenuAsync()
                }

                let openFunctionsSelector () = task {
                    try
                        isLoading.Publish true
                        let! fns = functionService.GetFunctions() |> ValueTask.map (Seq.map _.Id)
                        let! results = functionService.GetKernelPlugins(fns)
                        transact (fun _ ->
                            functions.Clear()
                            functions.AddRange results
                            isLoading.Value <- false
                        )
                    with ex ->
                        dialogService.ShowMessage("Error", ex.Message, severity = Severity.Error)
                        isLoading.Publish false
                }

                adapt {
                    let! selectedFunctions = selectedFunctions
                    MudButtonGroup'' {
                        Size Size.Small
                        Variant(if selectedFunctions.Count > 0 then Variant.Outlined else Variant.Text)
                        MudMenu'' {
                            AnchorOrigin Origin.TopLeft
                            TransformOrigin Origin.BottomLeft
                            ActivatorContent(
                                adapt {
                                    match! isLoading with
                                    | false -> fragment {
                                        if selectedFunctions.Count = 0 then
                                            MudIconButton'' {
                                                OnClick(ignore >> openFunctionsSelector)
                                                Icon Icons.Material.Filled.Construction
                                            }
                                        else
                                            MudButton'' {
                                                OnClick(ignore >> openFunctionsSelector)
                                                Color Color.Primary
                                                Variant Variant.Text
                                                StartIcon Icons.Material.Filled.Construction
                                                if selectedFunctions.Count = 1 then
                                                    selectedFunctions[0].Name
                                                else
                                                    selectedFunctions.Count
                                            }
                                      }
                                    | true -> MudProgressCircular'' {
                                        Size Size.Small
                                        Indeterminate
                                        Color Color.Primary
                                      }
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
                                    let! functions = functions
                                    let! functionFilter = functionsFilter
                                    for fns in functions do
                                        let isPluginMatch =
                                            fns.Name.Contains(functionFilter, StringComparison.OrdinalIgnoreCase)
                                            || fns.Description.Contains(functionFilter, StringComparison.OrdinalIgnoreCase)
                                        let filteredFunctions =
                                            fns
                                            |> Seq.filter (fun y ->
                                                y.Name.Contains(functionFilter, StringComparison.OrdinalIgnoreCase)
                                                || y.Description.Contains(functionFilter, StringComparison.OrdinalIgnoreCase)
                                            )
                                            |> Seq.toList
                                        let availableFunctions =
                                            match filteredFunctions with
                                            | [] -> fns :> seq<KernelFunction>
                                            | x -> x
                                        if String.IsNullOrEmpty functionFilter || isPluginMatch || filteredFunctions.Length > 0 then
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
                                                    fns.Name
                                                }
                                                MudIcon'' {
                                                    Size Size.Small
                                                    Icon Icons.Material.Filled.KeyboardArrowDown
                                                }
                                            }
                                            for fn in availableFunctions do
                                                match firstMatchFunction with
                                                | null -> firstMatchFunction <- fn
                                                | _ -> ()

                                                let color' =
                                                    if
                                                        selectedFunctions |> Seq.exists (fun x -> x.Name = fn.Name && x.PluginName = fn.PluginName)
                                                    then
                                                        Color.Primary
                                                    else
                                                        Color.Default

                                                MudMenuItem'' {
                                                    OnClick(fun _ -> selectFunction fn)
                                                    MudText'' {
                                                        Color color'
                                                        fn.Name
                                                    }
                                                    MudText'' {
                                                        style { textOverflowWithMaxLines 3 }
                                                        Typo Typo.body2
                                                        Color color'
                                                        fn.Description
                                                    }
                                                }
                                }
                            }
                            MudDivider''
                            div {
                                style { padding 8 }
                                adapt {
                                    let! v, setV = functionsFilter.WithSetter()
                                    MudTextField'' {
                                        Value v
                                        ValueChanged(fun v ->
                                            firstMatchFunction <- null
                                            setV v
                                        )
                                        Placeholder "Filter functions"
                                        AutoFocus
                                        DebounceInterval 400
                                        OnKeyUp(fun e -> task {
                                            if e.Key = "Enter" then
                                                match firstMatchFunction with
                                                | null -> ()
                                                | fn -> do! selectFunction fn
                                        })
                                    }
                                }
                            }
                        }
                        region {
                            if selectedFunctions.Count > 0 then
                                MudIconButton'' {
                                    Size Size.Small
                                    Variant Variant.Text
                                    Icon Icons.Material.Filled.Close
                                    OnClick(ignore >> clearSelections)
                                }
                        }
                    }
                }
        )
