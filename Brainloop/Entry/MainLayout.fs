namespace Brainloop.Entry

open System
open System.Threading
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web
open MudBlazor
open Blazored.LocalStorage
open Fun.Result
open Fun.Blazor
open Brainloop.Loop
open Brainloop.Model
open Brainloop.Agent


type MainLayout(shareStore: IShareStore, globalStore: IGlobalStore, localStorageService: ILocalStorageService) as this =
    inherit LayoutComponentBase()

    let cancellationTokenSource = new CancellationTokenSource()

    let mutable isModelAndAgentsReady = false
    let mutable isThemeTypeLoaded = false
    let mutable themeProviderRef: MudThemeProvider | null = null


    let welcomeWarningWithLink link (msg: NodeRenderFragment) = div {
        style {
            displayFlex
            flexDirectionColumn
            justifyContentCenter
            alignContentCenter
            gap 20
            backgroundColor "var(--mud-palette-background)"
            positionAbsolute
            top 0
            left 0
            right 0
            height "100%"
        }
        MudText'' {
            Color Color.Primary
            Typo Typo.h5
            Align Align.Center
            "Welcome to Brainloop!"
        }
        MudLink'' {
            Href link
            Underline Underline.Always
            MudText'' {
                Color Color.Primary
                Typo Typo.h6
                Align Align.Center
                msg
            }
        }
        MudLink'' {
            Href "/"
            Underline Underline.Always
            MudText'' {
                Color Color.Primary
                Align Align.Center
                "Refresh if you already setup"
            }
        }
    }

    let welcomeContents =
        html.inject (fun (modelService: IModelService, agentService: IAgentService) -> task {
            if isModelAndAgentsReady then
                return html.none
            else
                let! models = modelService.GetModelsWithCache()
                if models.IsEmpty then
                    return welcomeWarningWithLink "/models" (span { "Please create some models then define an agent to use it." })
                else
                    let! agents = agentService.GetAgentsWithCache()
                    if agents.IsEmpty then
                        return welcomeWarningWithLink "/agents" (span { "Please create some agents to use your models." })
                    else
                        isModelAndAgentsReady <- true
                        // Put the loops under the bottom always, so it will not be rendered on navigation
                        return html.none
        })

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
            welcomeContents
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
