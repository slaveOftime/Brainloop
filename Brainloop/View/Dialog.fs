[<AutoOpen>]
module Fun.Blazor.Dialogs

open System
open System.Threading.Tasks
open System.Diagnostics.CodeAnalysis
open FSharp.Data.Adaptive
open Microsoft.AspNetCore.Components
open Microsoft.Extensions.DependencyInjection
open IcedTasks
open MudBlazor
open Fun.Result
open Fun.Blazor
open Fun.Blazor.Operators

type MudDialog' with

    [<CustomOperation "Header">]
    member inline _.Header([<InlineIfLambda>] render: AttrRenderFragment, close: unit -> unit) =
        render
        ==> (MudDialog'' {
            TitleContent(
                MudIconButton'' {
                    style {
                        positionAbsolute
                        top 0
                        right 0
                        backgroundColor "var(--mud-palette-background)"
                    }
                    Icon Icons.Material.Filled.Close
                    OnClick(ignore >> close)
                }
            )
            asAttrRenderFragment
        })


    [<CustomOperation "Header">]
    member inline _.Header([<InlineIfLambda>] render: AttrRenderFragment, title: NodeRenderFragment, close: unit -> unit) =
        render
        ==> (MudDialog'' {
            TitleContent(
                fragment {
                    div {
                        class' "dialog-title"
                        title
                    }
                    MudIconButton'' {
                        style {
                            positionAbsolute
                            top 0
                            right 0
                            backgroundColor "var(--mud-palette-background)"
                        }
                        Icon Icons.Material.Filled.Close
                        OnClick(ignore >> close)
                    }
                }
            )
            asAttrRenderFragment
        })

    [<CustomOperation "Header">]
    member inline this.Header([<InlineIfLambda>] render: AttrRenderFragment, title: string, close: unit -> unit) =
        if String.IsNullOrEmpty title then
            this.Header(render, close)
        else
            this.Header(render, html.text title, close)


    [<CustomOperation "EmptyHeader">]
    member inline this.EmptyHeader([<InlineIfLambda>] render: AttrRenderFragment) =
        render
        ==> (MudDialog'' {
            TitleContent ""
            asAttrRenderFragment
        })


type FunDialogProps = { Close: unit -> unit; Options: DialogOptions }


type FunDialog() =
    inherit FunBlazorComponent()

    let mutable dialogView = None

    [<CascadingParameter>]
    member val MudDialogInstance = Unchecked.defaultof<IMudDialogInstance> with get, set

    [<Parameter>]
    member val RenderFn = Unchecked.defaultof<FunDialogProps -> NodeRenderFragment> with get, set

    [<Parameter>]
    member val CascadingServiceProvider = Unchecked.defaultof<IServiceProvider> with get, set


    member this.CloseDialog() = this.InvokeAsync(fun _ -> this.MudDialogInstance.Close()) |> ignore


    override this.Render() = region {
        match dialogView with
        | None ->
            let newView =
                this.RenderFn {
                    Close = this.CloseDialog
                    Options = this.MudDialogInstance.Options
                }

            let finalView =
                if box this.CascadingServiceProvider = null then
                    newView
                else
                    CascadingValue'() {
                        Name Internal.FunBlazorScopedServicesName
                        IsFixed true
                        Value this.CascadingServiceProvider
                        newView
                    }

            dialogView <- Some finalView

            finalView

        | Some x -> x
    }


type IDialogService with

    [<DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof<FunDialog>)>]
    member this.Show(title, render: FunDialogProps -> NodeRenderFragment, ?options, ?cascadingServiceProvider: IServiceProvider) =
        let options = options |> Option.defaultWith (fun _ -> DialogOptions())
        let parameters = DialogParameters()
        parameters.Add("RenderFn", render)
        parameters.Add("CascadingServiceProvider", Option.toObj cascadingServiceProvider)
        this.ShowAsync<FunDialog>(title, parameters, options)

    member this.Show(title, render: FunDialogProps -> NodeRenderFragment) = this.Show(title, render, DialogOptions()) |> ignore

    member this.Show(render: FunDialogProps -> NodeRenderFragment) = this.Show("", render, DialogOptions()) |> ignore

    member this.Show(options, render: FunDialogProps -> NodeRenderFragment) = this.Show("", render, options) |> ignore


    member this.ShowConfirm
        (
            titleStr: string,
            contentStr: string,
            confirmStr: string,
            cancelStr: string,
            confirmFn: unit -> Task<unit>,
            ?onCancel: unit -> unit,
            ?severity: Severity
        ) =
        this.Show(
            DialogOptions(BackdropClick = true),
            fun ctx ->
                html.inject (fun () ->
                    let isLoading = cval false
                    let severity = defaultArg severity Severity.Warning

                    let onCancel () =
                        ctx.Close()
                        onCancel |> Option.iter (fun fn -> fn ())

                    MudDialog'' {
                        Header titleStr onCancel
                        DialogContent(
                            adapt {
                                let! isLoading' = isLoading
                                html.fragment [
                                    MudAlert'' {
                                        Severity severity
                                        style { minWidth 300 }
                                        contentStr
                                    }
                                    if isLoading' then
                                        MudProgressLinear'' {
                                            Indeterminate
                                            Color Color.Primary
                                        }
                                ]
                            }
                        )
                        DialogActions(
                            adapt {
                                let! isLoading' = isLoading
                                html.fragment [|
                                    MudButton'' {
                                        Disabled isLoading'
                                        OnClick(fun _ -> onCancel ())
                                        cancelStr
                                    }
                                    MudButton'' {
                                        Disabled isLoading'
                                        Variant Variant.Filled
                                        Color(
                                            match severity with
                                            | Severity.Info -> Color.Info
                                            | Severity.Success -> Color.Success
                                            | Severity.Warning -> Color.Warning
                                            | Severity.Error -> Color.Error
                                            | _ -> Color.Primary
                                        )
                                        OnClick(fun _ ->
                                            task {
                                                isLoading.Publish true
                                                do! confirmFn ()
                                                isLoading.Publish false
                                                ctx.Close()
                                            }
                                            |> ignore
                                        )
                                        confirmStr
                                    }
                                |]
                            }
                        )
                    }
                )
        )


    member this.ShowConfirm(titleStr: string, contentStr: string, confirmStr: string, cancelStr: string, ?severity) =
        let tcs = TaskCompletionSource<bool>()
        this.ShowConfirm(
            titleStr,
            contentStr,
            confirmStr,
            cancelStr,
            (fun _ ->
                tcs.SetResult(true)
                Task.retn ()
            ),
            onCancel = (fun _ -> tcs.SetResult(false)),
            ?severity = severity
        )
        tcs.Task


    member this.ShowMessage(titleStr: string, contentStr: string, ?severity: Severity) =
        this.Show(fun ctx ->
            html.inject (fun () -> MudDialog'' {
                Header titleStr ctx.Close
                DialogContent(
                    MudAlert'' {
                        Severity(defaultArg severity Severity.Info)
                        style { minWidth 300 }
                        contentStr
                    }
                )
            })
        )

    member this.PreviewHtml(htmlDoc: string) =
        this.Show(
            DialogOptions(FullWidth = true, MaxWidth = MaxWidth.Large),
            fun props -> MudDialog'' {
                Header "Html Preview" props.Close
                DialogContent(
                    iframe {
                        style {
                            width "100%"
                            height "calc(100vh - 200px)"
                        }
                        srcdoc htmlDoc
                    }
                )
            }
        )

    member this.ShowLoading(?message: NodeRenderFragment) = valueTask {
        let closeHandler = TaskCompletionSource<unit -> unit>()

        this.Show(
            DialogOptions(FullScreen = true),
            fun ctx ->
                html.inject (fun (hook: IComponentHook) ->
                    hook.AddFirstAfterRenderTask(fun _ -> task {
                        do! Async.Sleep 10
                        closeHandler.SetResult ctx.Close
                    })

                    div {
                        //onclick (ignore >> ctx.Close)
                        style {
                            displayFlex
                            flexDirectionColumn
                            alignItemsCenter
                            justifyContentCenter
                            positionFixed
                            top 0
                            right 0
                            bottom 0
                            left 0
                            backgroundColor "var(--mud-palette-overlay-dark)"
                        }
                        MudPaper'' {
                            style {
                                displayFlex
                                flexDirectionColumn
                                alignItemsCenter
                                justifyContentCenter
                                gap 12
                                padding 40
                                maxWidth 250
                            }
                            MudProgressCircular'' {
                                Color Color.Primary
                                Indeterminate
                            }
                            defaultArg message html.none
                        }
                    }
                )
        )

        return! closeHandler.Task
    }


type IComponentHook with

    /// With this we can pass parent component's ScopedServiceProvider down to the dialog content
    member hook.ShowDialog(title, render: FunDialogProps -> NodeRenderFragment) =
        let dialog = hook.ServiceProvider.GetRequiredService<IDialogService>()
        dialog.Show(title, render, DialogOptions(), hook.ScopedServiceProvider) |> ignore

    /// With this we can pass parent component's ScopedServiceProvider down to the dialog content
    member hook.ShowDialog(render: FunDialogProps -> NodeRenderFragment) =
        let dialog = hook.ServiceProvider.GetRequiredService<IDialogService>()
        dialog.Show("", render, DialogOptions(), hook.ScopedServiceProvider) |> ignore

    /// With this we can pass parent component's ScopedServiceProvider down to the dialog content
    member hook.ShowDialog(options, render: FunDialogProps -> NodeRenderFragment) =
        let dialog = hook.ServiceProvider.GetRequiredService<IDialogService>()
        dialog.Show("", render, options, hook.ScopedServiceProvider) |> ignore
