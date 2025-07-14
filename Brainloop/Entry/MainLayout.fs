namespace Brainloop.Entry

open System
open System.Threading
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web
open MudBlazor
open Blazored.LocalStorage
open Fun.Result
open Fun.Blazor
open Brainloop.Db
open Brainloop.Loop


type MainLayout(shareStore: IShareStore, globalStore: IGlobalStore, localStorageService: ILocalStorageService) as this =
    inherit LayoutComponentBase()

    let cancellationTokenSource = new CancellationTokenSource()

    let mutable isModelAndAgentsReady = false
    let mutable isThemeTypeLoaded = false
    let mutable themeProviderRef: MudThemeProvider | null = null


    let themeContents = adaptiview (key = "theme-scripts") {
        let! theme = globalStore.Theme
        let! isDarkMode, setIsDarkMode = shareStore.IsDarkMode.WithSetter()
        MudThemeProvider'' {
            Theme theme
            IsDarkMode isDarkMode
            IsDarkModeChanged(fun x ->
                if isThemeTypeLoaded && shareStore.ThemeType.Value = ThemeType.System then
                    setIsDarkMode x
            )
            ObserveSystemThemeChange
            ref (fun x -> themeProviderRef <- x)
        }
        match isDarkMode with
        | true ->
            stylesheet (this.MapAsset("css/github-markdown-dark.css"))
            stylesheet (this.MapAsset("css/prism-vsc-dark-plus.css"))
            script {
                key "dark-script"
                $$"""
                    mermaid.initialize({
                        securityLevel: 'loose',
                        theme: 'dark',
                        startOnLoad: false,
                    });
                    """
            }
        | _ ->
            stylesheet (this.MapAsset("css/github-markdown-light.css"))
            stylesheet (this.MapAsset("css/prism-vs.css"))
            script {
                key "light-script"
                $$"""
                    mermaid.initialize({
                        securityLevel: 'loose',
                        theme: 'neutral',
                        startOnLoad: false,
                    });
                    """
            }
    }

    let initialState =
        html.inject (
            "initial-state",
            fun (hook: IComponentHook) ->
                hook.AddFirstAfterRenderTask(fun _ -> task {
                    hook.AddDisposes [|
                        hook.ShareStore.CurrentLoop.AddLazyCallback(
                            function
                            | ValueNone -> localStorageService.RemoveItemAsync("current-loop-id") |> ignore
                            | ValueSome loop -> localStorageService.SetItemAsync("current-loop-id", loop.Id) |> ignore
                        )

                        hook.ShareStore.ThemeType.AddLazyCallback(fun x ->
                            match x with
                            | ThemeType.System ->
                                match themeProviderRef with
                                | null -> ()
                                | ref -> ref.GetSystemDarkModeAsync() |> Task.map shareStore.IsDarkMode.Publish |> ignore
                            | ThemeType.Dark -> shareStore.IsDarkMode.Publish(true)
                            | ThemeType.Light -> shareStore.IsDarkMode.Publish(false)

                            hook.SaveThemeType(x) |> ignore
                        )

                        hook.ShareStore.MaxActiveLoops.AddLazyCallback(fun x -> localStorageService.SetItemAsync("max-active-loops", x) |> ignore)
                    |]

                    try
                        do! hook.LoadThemeType()
                        do! hook.LoadCurrentLoop()
                        do! hook.LoadMaxActiveLoops()
                    with _ ->
                        ()

                    isThemeTypeLoaded <- true
                    this.StateHasChanged()

                    match themeProviderRef with
                    | null -> ()
                    | ref ->
                        if hook.ShareStore.ThemeType.Value = ThemeType.System then
                            let! isDark = ref.GetSystemDarkModeAsync()
                            shareStore.IsDarkMode.Publish(isDark)
                })

                html.none
        )

    let mainContent = ErrorBoundary'' {
        key "main-content"
        ErrorContent(fun error -> MudAlert'' {
            Severity Severity.Error
            error.ToString()
        })
        NavMenu.Create()
        div {
            style {
                displayFlex
                flexDirectionColumn
                overflowHidden
                positionRelative
                height "100%"
            }
            div {
                key "loops"
                style {
                    positionAbsolute
                    top 0
                    right 0
                    bottom 0
                    left 0
                    displayFlex
                    flexDirectionColumn
                    overflowHidden
                    height "100%"
                    zIndex 0
                }
                Brainloop.Loop.LoopsView.Create()
            }
            WelcomeView.Create(isModelAndAgentsReady, fun x -> isModelAndAgentsReady <- x)
            match this.Body with
            | null -> ()
            | x -> x
        }
    }

    let content = div {
        style {
            positionAbsolute
            top 0
            right 0
            bottom 0
            left 0
            displayFlex
            flexDirectionColumn
            overflowHidden
        }

        MudPopoverProvider''
        MudSnackbarProvider''
        MudDialogProvider''

        initialState

        region {
            if isThemeTypeLoaded then
                stylesheet (this.MapAsset("_content/MudBlazor/MudBlazor.min.css"))
                stylesheet (this.MapAsset("css/google-font.css"))
                stylesheet (this.MapAsset("css/github-markdown.css"))
                stylesheet (this.MapAsset("excalidraw/index-Cc5PzV2C.css"))
                themeContents
                stylesheet (this.MapAsset("css/site.css"))
                mainContent

            else
                MudProgressLinear'' {
                    Color Color.Primary
                    Indeterminate
                }
        }
    }


    member _.MapAsset(x) = base.Assets[x]

    member _.StateHasChanged() = base.StateHasChanged()


    override _.BuildRenderTree(builder) = content.Invoke(this, builder, 0) |> ignore


    interface IDisposable with
        member _.Dispose() = cancellationTokenSource.Cancel()
