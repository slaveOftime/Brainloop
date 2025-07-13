#nowarn "0020"

namespace Microsoft.Extensions.DependencyInjection

open System
open System.IO
open System.Runtime.CompilerServices
open Microsoft.SemanticKernel.Data
open Brainloop.Options
open Brainloop.Memory


[<Extension>]
type MemoryDIExtensions =
    [<Extension>]
    static member AddMemory(services: IServiceCollection, appOptions: AppOptions) =
        services.AddScoped<IDocumentService, DocumentService>()

        services.AddScoped<MemoryService>()
        services.AddScoped<ITextSearch>(fun sp -> sp.GetRequiredService<MemoryService>())
        services.AddScoped<IMemoryService>(fun sp -> sp.GetRequiredService<MemoryService>())

        match appOptions.VectorDbProvider with
        | "MsSqlServer" -> services.AddSqlServerVectorStore(fun _ -> appOptions.VectorDbConnectionString) |> ignore
        | "SqlLite" ->
            try
                if OperatingSystem.IsLinux() then
                    Serilog.Log.Logger.Information("Copy vec0.so for embedding")
                    File.Copy("vec0.so", "/usr/lib/vec0.so", overwrite = false)
            with ex ->
                Serilog.Log.Logger.Error(ex, "Copy sqlite vec0.so to")
            services.AddSqliteVectorStore(fun _ -> appOptions.VectorDbConnectionString) |> ignore
        | "PostgreSQL" -> services.AddPostgresVectorStore(appOptions.VectorDbConnectionString) |> ignore
        | "Qdrant" ->
            let uri, apiKey =
                match appOptions.VectorDbConnectionString.Split(";") with
                | [||] -> failwith "Qdrant connection string is empty"
                | [| x |] -> Uri(x), None
                | kvs ->
                    let kvs =
                        kvs
                        |> Seq.map (fun x ->
                            let kv = x.Split("=")
                            if kv.Length <> 2 then failwith $"Invalid Qdrant connection string: {x}"
                            kv[0], kv[1]
                        )
                        |> Map.ofSeq
                    Uri(kvs["Endpoint"]), kvs |> Map.tryFind "Key"
            let https = "https".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)
            services.AddQdrantVectorStore(uri.Host, port = uri.Port, https = https, ?apiKey = apiKey) |> ignore
        //| "Chroma" ->
        //    services.AddTransient<IMemoryStore>(fun sp ->
        //        Connectors.Chroma.ChromaMemoryStore(
        //            appOptions.VectorDbConnectionString,
        //            loggerFactory = sp.GetRequiredService<ILoggerFactory>()
        //        )
        //    )
        //    |> ignore
        //| "Milvus" ->
        //    services.AddTransient<IMemoryStore>(fun sp ->
        //        let uri = Uri(appOptions.VectorDbConnectionString)
        //        let https = "https".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase)
        //        new Connectors.Milvus.MilvusMemoryStore(
        //            uri.Host,
        //            port = uri.Port,
        //            ssl = https,
        //            loggerFactory = sp.GetRequiredService<ILoggerFactory>()
        //        )
        //    )
        //    |> ignore
        | _ -> failwith $"Unsopported vector provider"
