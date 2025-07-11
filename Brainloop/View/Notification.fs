[<AutoOpen>]
module Fun.Blazor.Notification

open System
open Microsoft.Extensions.Logging
open MudBlazor
open Fun.Blazor
open Fun.Result


type ISnackbar with
    member snackbar.ShowMessage(message: string, ?severity: Severity, ?title: string) =
        let severity = defaultArg severity Severity.Info
        snackbar.Add(
            html.renderFragment (
                div {
                    match title with
                    | Some(SafeString t) -> MudText'' {
                        Color Color.Inherit
                        Typo Typo.subtitle1
                        t
                      }
                    | _ -> ()
                    MudText'' {
                        Color Color.Inherit
                        Typo Typo.body2
                        message
                    }
                }
            ),
            severity = severity
        )
        |> ignore


    member snackbar.ShowMessage(ex: Exception, logger: ILogger) =
        logger.LogError(ex, "Exception happened")
        snackbar.Add(
            html.renderFragment (
                div {
                    MudText'' {
                        Color Color.Inherit
                        Typo Typo.subtitle1
                        ex.Message
                    }
                    MudText'' {
                        Color Color.Inherit
                        Typo Typo.body2
                        string ex
                    }
                }
            ),
            severity = Severity.Error
        )
        |> ignore
