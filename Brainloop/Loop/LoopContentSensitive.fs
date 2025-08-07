namespace Brainloop.Loop

open System
open System.Text.Json
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open Fun.Blazor
open Brainloop.Share


type LoopContentSensitive =

    // Will save to db after encrypted
    static member EncryptDialog(contentWrapper: LoopContentWrapper, onClose: unit -> unit, ?onEncrypted: unit -> unit) : NodeRenderFragment =
        html.inject (fun (loopContentService: ILoopContentService, dialogService: IDialogService) ->
            let pass = cval ""
            let confirmPass = cval ""

            let encrypt () = task {
                if not (String.IsNullOrWhiteSpace pass.Value) && pass.Value = confirmPass.Value then
                    try
                        let secret = AlwaysSecure.aesEncrypt pass.Value (toJson contentWrapper.Items.Value)
                        transact (fun _ ->
                            contentWrapper.Items.Clear()
                            contentWrapper.Items.Add(LoopContentItem.Secret secret) |> ignore
                            contentWrapper.IsSecret.Value <- true
                        )
                        let! _ = loopContentService.UpsertLoopContent(contentWrapper)
                        onClose ()
                        onEncrypted |> Option.iter (fun fn -> fn ())
                    with ex ->
                        dialogService.ShowMessage("Failed to lock the content", ex.Message, severity = Severity.Error)
            }

            MudDialog'' {
                Header "Lock the content" onClose
                DialogContent(
                    MudFocusTrap'' {
                        div {
                            style {
                                displayFlex
                                flexDirectionColumn
                                gap 12
                            }
                            adapt {
                                let! binding = pass.WithSetter()
                                MudTextField'' {
                                    Label "Password"
                                    Value' binding
                                    Variant Variant.Outlined
                                    InputType InputType.Password
                                    FullWidth
                                    AutoFocus
                                    Adornment Adornment.Start
                                    AdornmentIcon Icons.Material.Filled.Password
                                }
                            }
                            adapt {
                                let! binding = confirmPass.WithSetter()
                                MudTextField'' {
                                    Label "Confirm Password"
                                    Value' binding
                                    Variant Variant.Outlined
                                    InputType InputType.Password
                                    FullWidth
                                    Adornment Adornment.Start
                                    AdornmentIcon Icons.Material.Filled.Password
                                    OnKeyUp(fun e -> task { if e.Key = "Enter" then do! encrypt () })
                                }
                            }
                        }
                    }
                )
                DialogActions [|
                    adapt {
                        let! pass = pass
                        let! confirmPass = confirmPass
                        MudButton'' {
                            Color Color.Primary
                            Variant Variant.Filled
                            Disabled(String.IsNullOrWhiteSpace pass || pass <> confirmPass)
                            OnClick(ignore >> encrypt)
                            "Lock"
                        }
                    }
                |]
            }
        )


    static member DecryptDialog(contentWrapper: LoopContentWrapper, secret: string, onClose: unit -> unit) : NodeRenderFragment =
        html.inject (
            struct ("secret", secret),
            fun (dialogService: IDialogService) ->
                let pass = cval ""
                let turnOffSecret = cval false

                let decrypt () =
                    try
                        let content = AlwaysSecure.aesDecrypt pass.Value secret
                        let items = fromJson<LoopContentItem[]> content |> ValueOption.defaultValue [||]
                        transact (fun _ ->
                            contentWrapper.Items.Clear()
                            contentWrapper.Items.AddRange(items)
                            contentWrapper.IsSecret.Value <- not turnOffSecret.Value
                        )
                        onClose ()
                        if not turnOffSecret.Value then
                            valueTask {
                                do! Async.Sleep(60_000 * 2)
                                // After some times, if the content is not changed and the secret is still on, we should restore it to original value
                                if content = (contentWrapper.Items |> AList.force |> Seq.toList |> toJson) then
                                    transact (fun _ ->
                                        contentWrapper.Items.Clear()
                                        contentWrapper.Items.Add(LoopContentItem.Secret secret) |> ignore
                                    )
                            }
                            |> ignore
                    with ex ->
                        dialogService.ShowMessage("Failed to unlock the content", ex.Message, severity = Severity.Error)

                MudDialog'' {
                    Header "Unlock the content" onClose
                    DialogContent(
                        MudFocusTrap'' {
                            adapt {
                                let! binding = pass.WithSetter()
                                MudTextField'' {
                                    Label "Password"
                                    Value' binding
                                    InputType InputType.Password
                                    Variant Variant.Outlined
                                    FullWidth
                                    AutoFocus
                                    Adornment Adornment.Start
                                    AdornmentIcon Icons.Material.Filled.Password
                                    OnKeyUp(fun e -> if e.Key = "Enter" then decrypt ())
                                }
                            }
                        }
                    )
                    DialogActions [|
                        adapt {
                            let! binding = turnOffSecret.WithSetter()
                            MudSwitch'' {
                                Label "Turn off protection"
                                Color Color.Error
                                Value' binding
                            }
                        }
                        MudButton'' {
                            Color Color.Primary
                            Variant Variant.Filled
                            OnClick(ignore >> decrypt)
                            "Unlock"
                        }
                    |]
                }
        )


    static member LockerView(contentWrapper: LoopContentWrapper, secret: string) : NodeRenderFragment =
        html.inject (fun (dialogService: IDialogService) -> MudPaper'' {
            style {
                padding 8
                displayFlex
                alignItemsCenter
                justifyContentCenter
            }
            Elevation 5
            MudIconButton'' {
                Icon Icons.Material.Filled.Lock
                Color Color.Warning
                OnClick(fun _ ->
                    dialogService.Show(
                        DialogOptions(MaxWidth = MaxWidth.ExtraSmall, FullWidth = true),
                        fun ctx -> LoopContentSensitive.DecryptDialog(contentWrapper, secret, ctx.Close)
                    )
                )
            }
        })
