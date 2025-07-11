namespace Brainloop.Settings

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Components
open Microsoft.AspNetCore.Components.Web
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open MudBlazor
open IcedTasks
open Fun.Result
open Fun.Blazor
open Fun.Blazor.Validators
open Brainloop.Db
open Brainloop.Loop
open Brainloop.Handler


[<Route "/settings">]
type SettingsPage(settingsService: ISettingsService, shareStore: IShareStore) as this =
    inherit FunComponent()

    member _.Header = fragment {
        PageTitle'' { "Settings" }
        SectionContent'' {
            SectionName Constants.NavActionsSectionName
            MudSpacer''
        }
        MudText'' {
            style { margin 20 0 24 0 }
            Typo Typo.h3
            Align Align.Center
            "Settings"
        }
    }

    member _.MemorySettingsFrom(memorySettings: MemorySettings, onSave: MemorySettings -> ValueTask<unit>) =
        let settingsForm =
            (new AdaptiveForm<_, string>(memorySettings))
                .AddValidators((fun x -> x.TokensOfChunk), false, [ minValue 50 (fun _ -> "Minimal size is 50") ])
                .AddValidators((fun x -> x.TokensOfChunkOverlap), false, [ minValue 5 (fun _ -> "Minimal size is 5") ])
                .AddValidators((fun x -> x.EmbeddingModelId), false, [ minValue 1 (fun _ -> "Memory model is required") ])
        fragment {
            MudItem'' {
                xs 6
                adapt {
                    let! binding = settingsForm.UseFieldWithErrors(fun x -> x.TokensOfChunk)
                    MudNumericField'' {
                        Label "Memory chunk size in tokens"
                        Value' binding
                    }
                }
            }
            MudItem'' {
                xs 6
                adapt {
                    let! binding = settingsForm.UseFieldWithErrors(fun x -> x.TokensOfChunkOverlap)
                    MudNumericField'' {
                        Label "Memory chunk overlap size in tokens"
                        Value' binding
                    }
                }
            }
            MudItem'' {
                xs 12
                Brainloop.Model.ModelSelector.Create(
                    settingsForm.UseField(fun x -> x.EmbeddingModelId),
                    required = true,
                    filter = (fun x -> x.CanHandleEmbedding)
                )
            }
            MudItem'' {
                xs 12
                html.inject (fun (serviceProvider: IServiceProvider) ->
                    let globalStore = serviceProvider.GetRequiredService<IGlobalStore>()
                    let dialogService = serviceProvider.GetRequiredService<IDialogService>()
                    adapt {
                        let! isRebuildingMemory = globalStore.IsRebuildingMemory
                        MudButton'' {
                            Color Color.Warning
                            Variant Variant.Filled
                            Disabled isRebuildingMemory
                            OnClick(fun _ ->
                                dialogService.ShowConfirm(
                                    "Rebuild memory",
                                    "Are you sure you want to rebuild memory? It will clear old memory and may take some time!",
                                    "Rebuild",
                                    "Cancel",
                                    fun _ -> task {
                                        do! onSave (settingsForm.GetValue())
                                        globalStore.IsRebuildingMemory.Publish(true)
                                        let rebuildMemoryHandler = serviceProvider.GetRequiredService<IRebuildMemoryHandler>()
                                        do! rebuildMemoryHandler.Handle()
                                        globalStore.IsRebuildingMemory.Publish(false)
                                    }
                                )
                            )
                            if isRebuildingMemory then "Rebuilding memory" else "Start to rebuild memory"
                        }
                        if isRebuildingMemory then
                            MudProgressLinear'' {
                                Indeterminate
                                Color Color.Primary
                            }
                    }
                )
            }
        }


    override _.Render() = div {
        id "agents-container"
        style {
            zIndex 1
            height "100%"
            overflowYAuto
            backgroundColor "var(--mud-palette-background)"
        }
        MudContainer'' {
            MaxWidth MaxWidth.Medium
            this.Header
            MudGrid'' {
                Spacing 4
                MudItem'' {
                    xs 12
                    adapt {
                        let! binding = shareStore.ThemeType.WithSetter()
                        MudSelect'' {
                            Label "Theme for every browser"
                            Value' binding
                            for item in [| ThemeType.System; ThemeType.Light; ThemeType.Dark |] do
                                MudSelectItem'' {
                                    Value item
                                    string item
                                }
                        }
                    }
                }
                MudItem'' {
                    xs 12
                    adapt {
                        let! maxActiveLoops = shareStore.MaxActiveLoops
                        MudNumericField'' {
                            Label "Max Active Loops for every browser"
                            Value maxActiveLoops
                            ValueChanged(fun x -> shareStore.MaxActiveLoops.Publish(x))
                        }
                    }
                }
                MudItem'' {
                    xs 12
                    MudDivider''
                }
                adapt {
                    let! settings =
                        settingsService.GetSettings()
                        |> ValueTask.map LoadingState.Loaded
                        |> ValueTask.toTask
                        |> AVal.ofTask LoadingState.Loading
                    match settings with
                    | LoadingState.Loaded settings -> region {
                        for setting in settings do
                            match setting.Type with
                            | AppSettingsType.MemorySettings x ->
                                this.MemorySettingsFrom(
                                    x,
                                    fun x -> { setting with Type = AppSettingsType.MemorySettings x } |> settingsService.UpsertSettings
                                )
                            MudItem'' {
                                xs 12
                                MudDivider''
                            }
                      }
                    | _ -> MudProgressLinear'' {
                        Indeterminate
                        Color Color.Primary
                      }
                }
            }
        }
    }
