namespace Brainloop.Function

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


[<Route "tools">]
type FunctionsPage(functionService: IFunctionService, snackbar: ISnackbar, dialog: IDialogService, JS: IJSRuntime, logger: ILogger<FunctionsPage>) as this
    =
    inherit FunBlazorComponent()

    let isCreating = cval false
    let isSaving = cval false
    let query = cval ""
    let functionsRefresher = cval 0
    let expandedGroup = cval ""


    let addValidators (form: AdaptiveForm<Function, string>) =
        form.AddValidators((fun x -> x.Name), false, [ Validators.required "Name is required" ])

    let createFunction () = task {
        isCreating.Publish(true)
        do! Async.Sleep 100
        do! JS.ScrollToElementTop("functions-container", "function-new-form", smooth = true)
    }

    [<Parameter; SupplyParameterFromQuery(Name = "query")>]
    member _.Filter
        with get () = query.Value
        and set (x: string) = query.Publish x


    member _.UpsertFunction(value: Function, isForCreating: bool) = task {
        isSaving.Publish true

        try
            do! functionService.UpsertFunction(value)
            transact (fun _ ->
                functionsRefresher.Value <- functionsRefresher.Value + 1
                isSaving.Value <- false
                if isForCreating then isCreating.Value <- false
            )
        with ex ->
            snackbar.ShowMessage(ex, logger)
            transact (fun _ -> isSaving.Value <- false)
    }


    member _.Header = fragment {
        PageTitle'' { "Tools" }
        SectionContent'' {
            SectionName Strings.NavActionsSectionName
            MudSpacer''
        }
        MudText'' {
            Typo Typo.h3
            Align Align.Center
            style { margin 20 0 24 0 }
            "Tools"
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
                        Placeholder "Search Tools"
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
                    OnClick(ignore >> createFunction)
                    "Create"
                }
            }
        }
    }


    member _.FunctionForm(func: Function, isForCreating: bool, groups) =
        let form = new AdaptiveForm<Function, string>(func) |> addValidators
        fragment {
            FunctionCard.Create(form, groups = groups)
            div {
                style {
                    displayFlex
                    justifyContentFlexEnd
                    gap 12
                    paddingTop 12
                }
                region {
                    let functionId = form.GetFieldValue(fun x -> x.Id)
                    if functionId > 0 then
                        MudButton'' {
                            OnClick(fun _ -> task {
                                let! result = dialog.ShowMessageBox("Warning", "Are you sure to delete this function?")
                                if result.HasValue && result.Value then
                                    try
                                        do! functionService.DeleteFunction(functionId)
                                        functionsRefresher.Publish((+) 1)
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
                            OnClick(fun _ -> if isForCreating then isCreating.Publish false else form.SetValue func)
                            "Cancel"
                        }
                        MudButton'' {
                            Variant Variant.Filled
                            Color Color.Primary
                            OnClick(fun _ -> this.UpsertFunction(form.GetValue(), isForCreating))
                            Disabled(isSaving' || errors.Length > 0)
                            "Save"
                        }
                }
            }
        }

    member _.FunctionsNewForm(groups) = adaptiview (key = "new-function") {
        match! isCreating with
        | true ->
            MudPaper'' {
                id "function-new-form"
                style {
                    padding 12
                    displayFlex
                    flexDirectionColumn
                    gap 12
                }
                Elevation 2
                this.FunctionForm(Function.Default, true, groups = groups)
            }
            div {
                style { padding 24 }
                MudDivider''
            }
        | _ -> ()
    }

    member _.FunctionPanel(func: Function, groups) = adaptiview (key = func.Id) {
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
                    func.Name
                    MudChipSet'' {
                        Size Size.Small
                        Color Color.Secondary
                        MudChip'' {
                            Color(
                                match func.Type with
                                | SYSTEM_FUNCTION -> Color.Primary
                                | _ -> Color.Default
                            )
                            match func.Type with
                            | FunctionType.SystemGetCurrentTime
                            | FunctionType.SystemRenderInIframe
                            | FunctionType.SystemSendHttp _
                            | FunctionType.SystemSearchMemory _
                            | FunctionType.SystemReadDocumentAsText
                            | FunctionType.SystemExecuteCommand _
                            | FunctionType.SystemGenerateImage _
                            | FunctionType.SystemCreateTaskForAgent
                            | FunctionType.SystemCreateScheduledTaskForAgent -> "System"
                            | FunctionType.Mcp(McpConfig.STDIO _) -> "MCP"
                            | FunctionType.Mcp(McpConfig.SSE _) -> "MCP"
                            | FunctionType.OpenApi _
                            | FunctionType.OpenApiUrl _ -> "OpenApi"
                        }
                    }
                }
            )
            region {
                if isExpanded then
                    html.inject (func, fun () -> this.FunctionForm(func, false, groups))
            }
        }
    }

    member _.FunctionsView = MudExpansionPanels'' {
        MultiExpansion
        adapt {
            let! _ = functionsRefresher
            let! functions = functionService.GetFunctions().AsTask() |> Task.map (Seq.sortBy (fun x -> x.Name)) |> AVal.ofTask Seq.empty
            let! query = query

            let filteredFunctions =
                functions
                |> Seq.filter (fun x ->
                    String.IsNullOrEmpty query
                    || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                )

            let groupedFunctions =
                filteredFunctions
                |> Seq.groupBy (fun x ->
                    match x.Group with
                    | null -> ""
                    | SafeString s -> s
                    | _ -> ""
                )
                |> Seq.sortBy fst

            let groups = groupedFunctions |> Seq.map fst |> Seq.toList

            let! expandedGroup, setExpandedGroup = expandedGroup.WithSetter()

            this.FunctionsNewForm(groups)
            for g, functions in groupedFunctions do
                match g with
                | SafeString g ->
                    div {
                        style {
                            displayFlex
                            alignItemsCenter
                            justifyContentCenter
                            paddingTop 24
                        }
                        MudButton'' {
                            EndIcon(
                                if g = expandedGroup then
                                    Icons.Material.Filled.ExpandLess
                                else
                                    Icons.Material.Filled.ExpandMore
                            )
                            OnClick(fun _ -> setExpandedGroup (if g = expandedGroup then "" else g))
                            g
                        }
                    }
                    if g = expandedGroup then
                        for func in functions do
                            this.FunctionPanel(func, groups)
                | _ ->
                    for dunc in functions do
                        this.FunctionPanel(dunc, groups)
        }
    }


    override _.Render() = div {
        id "functions-container"
        style {
            zIndex 1
            height "100%"
            overflowYAuto
            backgroundColor "var(--mud-palette-background)"
        }
        MudContainer'' {
            MaxWidth MaxWidth.Medium
            this.Header
            this.FunctionsView
        }
    }
