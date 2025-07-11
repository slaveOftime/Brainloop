namespace Brainloop.Settings

open System.Threading.Tasks
open IcedTasks
open Brainloop.Db


type ISettingsService =
    abstract member GetSettings: unit -> ValueTask<AppSettings list>
    abstract member UpsertSettings: AppSettings -> ValueTask<unit>


type SettingsService(dbService: IDbService) =
    interface ISettingsService with
        member _.GetSettings() =
            dbService.DbContext.Select<AppSettings>().ToListAsync()
            |> ValueTask.ofTask
            |> ValueTask.map (fun settings -> [|
                yield! settings

                // If there is no memory settings, append a default one
                if
                    settings
                    |> Seq.exists (
                        function
                        | { Type = AppSettingsType.MemorySettings _ } -> true
                    )
                    |> not
                then
                    {
                        AppSettings.Default with
                            Type = AppSettingsType.MemorySettings MemorySettings.Default
                            TypeName = nameof AppSettingsType.MemorySettings
                    }
            |])
            |> ValueTask.map (Seq.sortBy _.TypeName >> Seq.toList)

        member _.UpsertSettings(settings) =
            dbService.DbContext.InsertOrUpdate<AppSettings>().SetSource(settings).ExecuteAffrowsAsync()
            |> ValueTask.ofTask
            |> ValueTask.map ignore
