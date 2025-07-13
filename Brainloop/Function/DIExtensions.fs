#nowarn "0020"

namespace Microsoft.Extensions.DependencyInjection

open System.IO
open System.Runtime.CompilerServices
open Microsoft.Extensions.DependencyInjection
open Quartz
open Brainloop.Options
open Brainloop.Function
open Brainloop.Function.SystemFunctions


[<Extension>]
type FunctionDIExtensions =
    [<Extension>]
    static member AddFunction(services: IServiceCollection, appOptions: AppOptions) =
        try
            match appOptions.DataDbProvider with
            | "MsSqlServer" ->
                use connection = new Microsoft.Data.SqlClient.SqlConnection(appOptions.DataDbConnectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- File.ReadAllText("Function/Quartz/tables_sqlServer.sql")
                command.ExecuteNonQuery() |> ignore

            | "SqlLite" ->
                use connection = new Microsoft.Data.Sqlite.SqliteConnection(appOptions.DataDbConnectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- File.ReadAllText("Function/Quartz/tables_sqlite.sql")
                command.ExecuteNonQuery() |> ignore

            | "PostgreSQL" ->
                use connection = new Npgsql.NpgsqlConnection(appOptions.DataDbConnectionString)
                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- File.ReadAllText("Function/Quartz/tables_postgres.sql")
                command.ExecuteNonQuery() |> ignore

            | x -> failwith $"Unsupported database provider {x}"
        with ex ->
            printfn "Quartz db init: %s" ex.Message


        services.AddQuartz(fun configurator ->
            configurator.UseSimpleTypeLoader()
            configurator.UsePersistentStore(fun options ->
                options.UseProperties <- true
                options.PerformSchemaValidation <- false
                options.UseSystemTextJsonSerializer()
                match appOptions.DataDbProvider with
                | "MsSqlServer" -> options.UseSqlServer(appOptions.DataDbConnectionString) |> ignore
                | "SqlLite" -> options.UseSQLite(appOptions.DataDbConnectionString) |> ignore
                | "PostgreSQL" -> options.UsePostgres(appOptions.DataDbConnectionString) |> ignore
                | x -> failwith $"Unsupported database provider {x}"
            )
        )

        services.AddQuartzHostedService(fun options -> options.WaitForJobsToComplete <- true)

        services.AddScoped<IFunctionService, FunctionService>()

        services.AddScoped<SystemSendHttpFunc>()
        services.AddScoped<SystemGenerateImageFunc>()
        services.AddScoped<SystemInvokeAgentFunc>()
        services.AddScoped<SystemExecuteCommandFunc>()
        services.AddScoped<SystemCreateTaskForAgentFunc>()
        services.AddScoped<SystemCreateScheduledTaskForAgentFunc>()
