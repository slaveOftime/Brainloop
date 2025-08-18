#nowarn "0020"

namespace Brainloop.Db

open System.Text.Json
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Brainloop.Options
open FreeSql
open FreeSql.Internal
open FreeSql.Internal.Model
open Fun.Result


type IDbService =
    abstract member DbContext: IFreeSql

    abstract member ModelRepo: IBaseRepository<Model>
    abstract member AgentRepo: IBaseRepository<Agent>
    abstract member LoopRepo: IBaseRepository<Loop>


type JsonTypeHandler<'T>(?contentLength: int) =
    inherit TypeHandler<'T>()

    override _.Serialize(value: 'T) =
        let json = JsonSerializer.Serialize(value, JsonSerializerOptions.createDefault ())
        if json.StartsWith "\"" && json.Length > 2 then
            json.Substring(1, json.Length - 2)
        else
            json

    override _.Deserialize(value: obj) =
        let json =
            let json = string value
            match json with
            | "null" -> json
            | SafeStringStartWith "[" -> json
            | SafeStringStartWith "{" -> json
            | SafeStringStartWith "\"" -> json
            | _ -> $"\"{json}\""
        JsonSerializer.Deserialize<'T>(json, JsonSerializerOptions.createDefault ())

    override _.FluentApi(col) = col.MapType(typeof<string>).StringLength(defaultArg contentLength -1) |> ignore


type DbService(appOptions: IOptions<AppOptions>, logger: ILogger<DbService>) =

    do
        Utils.TypeHandlers.TryAdd(typeof<ModelProvider>, JsonTypeHandler<ModelProvider>(contentLength = 200))
        Utils.TypeHandlers.TryAdd(typeof<ModelApiProps voption>, JsonTypeHandler<ModelApiProps voption>())
        Utils.TypeHandlers.TryAdd(typeof<AgentType>, JsonTypeHandler<AgentType>(contentLength = 100))
        Utils.TypeHandlers.TryAdd(typeof<AgentFunctionTarget>, JsonTypeHandler<AgentFunctionTarget>(contentLength = 100))
        Utils.TypeHandlers.TryAdd(typeof<FunctionType>, JsonTypeHandler<FunctionType>())
        Utils.TypeHandlers.TryAdd(typeof<LoopContentAuthorRole>, JsonTypeHandler<LoopContentAuthorRole>(contentLength = 100))
        Utils.TypeHandlers.TryAdd(typeof<NotificationSource>, JsonTypeHandler<NotificationSource>(contentLength = 200))
        Utils.TypeHandlers.TryAdd(typeof<AppSettingsType>, JsonTypeHandler<AppSettingsType>())
        ()

    let settingsSql =
        lazy
            (let dataType =
                match appOptions.Value.DataDbProvider with
                | "MsSqlServer" -> FreeSql.DataType.SqlServer
                | "PostgreSQL" -> FreeSql.DataType.PostgreSQL
                | "SqlLite" -> FreeSql.DataType.Sqlite
                | x -> failwith $"Unsupported database provider {x}"

             let db =
                 FreeSql
                     .FreeSqlBuilder()
                     .UseConnectionString(dataType, appOptions.Value.DataDbConnectionString)
                     // As this is turn on for auto migrate db schema, so it is very important to make the db change backward compatible
                     .UseAutoSyncStructure(true)
                     .UseMonitorCommand(
                         (fun cmd -> logger.LogDebug("Executing SQL command: {CommandText}", cmd.CommandText)),
                         executed = (fun cmd _ -> logger.LogDebug("Executed SQL command: {CommandText}", cmd.CommandText))
                     )
                     .Build()

             db.CodeFirst
                 .ConfigEntity<Function>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Property(fun x -> x.Type).StringLength(500)
                     ()
                 )
                 .ConfigEntity<Model>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Property(fun x -> x.Provider).StringLength(500)
                     ()
                 )
                 .ConfigEntity<Agent>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Property(fun x -> x.Prompt).StringLength(-1)
                     e.Navigate((fun x -> x.AgentModels), null)
                     e.Navigate((fun x -> x.AgentFunctions), null)
                     ()
                 )
                 .ConfigEntity<AgentModel>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Index("AgentIdModelId", "AgentId,ModelId", true)
                     ()
                 )
                 .ConfigEntity<AgentFunctionWhitelist>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     ()
                 )
                 .ConfigEntity<AgentFunction>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     ()
                 )
                 .ConfigEntity<Loop>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Property(fun x -> x.Description).StringLength(-1)
                     e.Navigate((fun x -> x.LoopContents), null)
                     ()
                 )
                 .ConfigEntity<LoopContent>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Property(fun x -> x.Content).StringLength(-1)
                     e.Property(fun x -> x.ErrorMessage).StringLength(-1)
                     e.Property(fun x -> x.DirectPrompt).StringLength(-1)
                     ()
                 )
                 .ConfigEntity<LoopCategory>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Property(fun x -> x.Name).StringLength(255)
                     e.Navigate((fun x -> x.Loops), null)
                     e.Index("Name", "Name", true)
                     ()
                 )
                 .ConfigEntity<Notification>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     ()
                 )
                 .ConfigEntity<AppSettings>(fun e ->
                     e.Property(fun x -> x.Id).IsPrimary(true).IsIdentity(true)
                     e.Index("TypeName", "TypeName", true)
                     ()
                 )

             db)


    interface IDbService with
        member _.DbContext = settingsSql.Value

        member _.ModelRepo = settingsSql.Value.GetRepository<Model>()
        member _.AgentRepo = settingsSql.Value.GetRepository<Agent>()
        member _.LoopRepo = settingsSql.Value.GetRepository<Loop>()
