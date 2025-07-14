[<AutoOpen>]
module Fun.Blazor.Stores

open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open FSharp.Data.Adaptive
open IcedTasks
open MudBlazor
open Fun.Blazor


[<RequireQualifiedAccess>]
type ThemeType =
    | Light
    | Dark
    | System


type IGlobalStore with

    member store.Theme =
        store.CreateCVal(
            nameof store.Theme,
            MudTheme(
                PaletteDark = PaletteDark(Primary = "#1c6a5e", Secondary = "#477a49", Surface = "#0c0f14", Background = "#0a0b0e"),
                PaletteLight = PaletteLight(Primary = "#1c6a5e", Secondary = "#477a49")
            )
        )

    member store.IsRebuildingMemory = store.CreateCVal(nameof store.IsRebuildingMemory, false)


type IShareStore with

    member store.ThemeType = store.CreateCVal(nameof store.ThemeType, ThemeType.System)

    member store.IsDarkMode = store.CreateCVal(nameof store.IsDarkMode, true)

    member store.Palette = adaptive {
        let globalStore = store.ServiceProvider.GetRequiredService<IGlobalStore>()
        let! theme = globalStore.Theme
        let! isDarkMode = store.IsDarkMode
        let palette: Palette = if isDarkMode then theme.PaletteDark else theme.PaletteLight
        return palette
    }


    member store.IsToolbarOpen = store.CreateCVal(nameof store.IsToolbarOpen, false)


[<RequireQualifiedAccess>]
module AVal =
    let inline ofValueTask (defaultValue: 'T) (ts: ValueTask<'T>) =
        let data = cval defaultValue
        ts |> ValueTask.map data.Publish |> ignore
        data :> aval<'T>