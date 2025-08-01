namespace Brainloop.Function

open System
open System.IO
open System.Linq
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.Data
open Microsoft.SemanticKernel.Plugins.OpenApi
open IcedTasks
open FSharp.Control
open Fun.Result
open Brainloop.Db
open Brainloop.Memory
open Brainloop.Function.SystemFunctions


type FunctionService
    (
        dbService: IDbService,
        documentService: IDocumentService,
        serviceProvider: IServiceProvider,
        textSearch: ITextSearch,
        memoryCache: IMemoryCache,
        loggerFactory: ILoggerFactory
    ) as this =

    member private _.CreateBaseHttpClient(?headers: Map<string, string>, ?proxy: string) =
        let httpClientHandler = new HttpClientHandler()

        match proxy with
        | Some(SafeString proxy) ->
            let webProxy = WebProxy(Uri(proxy))
            httpClientHandler.UseProxy <- true
            httpClientHandler.Proxy <- webProxy
        | _ -> ()

        let httpClient = new HttpClient(httpClientHandler)

        match headers with
        | Some headers ->
            for KeyValue(key, value) in headers do
                httpClient.DefaultRequestHeaders.Add(key, value)
        | _ -> ()

        httpClient

    member private _.GetKernelPluginsAndSystemFunctions(ids: int seq, ?agentId: int, ?cancellationToken: CancellationToken) = valueTask {
        let plugins = Collections.Generic.Dictionary<int, KernelPlugin>()
        let systemFunctions = Collections.Generic.Dictionary<int, KernelFunction>()

        let db = dbService.DbContext
        let! functionsInDb =
            db.Queryable<Function>().Where(fun (x: Function) -> ids.Contains(x.Id)).ToListAsync(?cancellationToken = cancellationToken)

        for fn in functionsInDb do
            match cancellationToken with
            | Some x when x.IsCancellationRequested -> ()
            | _ ->
                match fn.Type with
                | FunctionType.Mcp config ->
                    let httpClient =
                        match config with
                        | McpConfig.SSE _ -> this.CreateBaseHttpClient(proxy = fn.Proxy) |> Some
                        | McpConfig.STDIO _ -> None
                    let! fns =
                        config.GetTools(
                            fn.McpFunctionName,
                            memoryCache,
                            loggerFactory,
                            ?httpClient = httpClient,
                            ?cancellationToken = cancellationToken
                        )
                    plugins[fn.Id] <-
                        KernelPluginFactory.CreateFromFunctions(
                            fn.Name.ToAscii(),
                            functions = (fns |> Seq.map (_.AsKernelFunction())),
                            description = "Call tools to finish some tasks according to its definition"
                        )

                | FunctionType.OpenApi config ->
                    let httpClient = this.CreateBaseHttpClient(headers = config.Headers, proxy = fn.Proxy)
                    use stream = new MemoryStream(Encoding.UTF8.GetBytes(config.JsonSchema))
                    let! plugin =
                        OpenApiKernelPluginFactory.CreateFromOpenApiAsync(
                            fn.Name.ToAscii(),
                            stream,
                            executionParameters = OpenApiFunctionExecutionParameters(httpClient = httpClient),
                            ?cancellationToken = cancellationToken
                        )
                    plugins[fn.Id] <- plugin

                | FunctionType.OpenApiUrl config ->
                    let httpClient = this.CreateBaseHttpClient(headers = config.Headers, proxy = fn.Proxy)
                    let! plugin =
                        OpenApiKernelPluginFactory.CreateFromOpenApiAsync(
                            fn.Name.ToAscii(),
                            new Uri(config.Url),
                            executionParameters = OpenApiFunctionExecutionParameters(httpClient = httpClient),
                            ?cancellationToken = cancellationToken
                        )
                    plugins[fn.Id] <- plugin

                | FunctionType.SystemGetCurrentTime ->
                    systemFunctions[fn.Id] <-
                        KernelFunctionFactory.CreateFromMethod(
                            Func<string>(fun () -> DateTimeOffset.Now.ToString()),
                            functionName = SystemFunction.GetCurrentTime,
                            description = fn.Description,
                            loggerFactory = loggerFactory
                        )
                | FunctionType.SystemRenderInIframe ->
                    systemFunctions[fn.Id] <-
                        KernelFunctionFactory.CreateFromMethod(
                            // We just need to return the original html to let UI to render it
                            // We may need to add some security check in the future
                            Func<string, string>(fun html -> html),
                            functionName = SystemFunction.RenderInIframe,
                            description = fn.Name + " " + fn.Description,
                            loggerFactory = loggerFactory
                        )
                | FunctionType.SystemSendHttp config ->
                    systemFunctions[fn.Id] <- serviceProvider.GetRequiredService<SystemSendHttpFunc>().Create(fn, config)
                | FunctionType.SystemSearchMemory config ->
                    systemFunctions[fn.Id] <-
                        KernelFunctionFactory.CreateFromMethod(
                            Func<string, Task<TextSearchResult list>>(fun query -> task {
                                let! results =
                                    textSearch.GetTextSearchResultsAsync(
                                        query,
                                        searchOptions = TextSearchOptions(Top = config.Top),
                                        ?cancellationToken = cancellationToken
                                    )
                                return! TaskSeq.toListAsync results.Results
                            }),
                            JsonSerializerOptions.createDefault (),
                            functionName = SystemFunction.SearchMemory,
                            description = fn.Name + " " + fn.Description,
                            loggerFactory = loggerFactory
                        )
                | FunctionType.SystemReadDocumentAsText ->
                    systemFunctions[fn.Id] <-
                        KernelFunctionFactory.CreateFromMethod(
                            Func<string, ValueTask<string>>(fun file -> documentService.ReadAsText(file)),
                            JsonSerializerOptions.createDefault (),
                            functionName = SystemFunction.ReadDocumentAsText,
                            description = fn.Name + " " + fn.Description,
                            loggerFactory = loggerFactory
                        )
                | FunctionType.SystemExecuteCommand config ->
                    systemFunctions[fn.Id] <-
                        serviceProvider.GetRequiredService<SystemExecuteCommandFunc>().Create(fn, config, ?cancellationToken = cancellationToken)
                | FunctionType.SystemCreateTaskForAgent ->
                    let! func =
                        serviceProvider
                            .GetRequiredService<SystemCreateTaskForAgentFunc>()
                            .Create(
                                fn,
                                excludedAgentIds = [
                                    match agentId with
                                    | None -> ()
                                    | Some x -> x
                                ]
                            )
                    systemFunctions[fn.Id] <- func
                | FunctionType.SystemCreateScheduledTaskForAgent ->
                    let! func =
                        serviceProvider
                            .GetRequiredService<SystemCreateScheduledTaskForAgentFunc>()
                            .Create(
                                fn,
                                excludedAgentIds = [
                                    match agentId with
                                    | None -> ()
                                    | Some x -> x
                                ],
                                ?cancellationToken = cancellationToken
                            )
                    systemFunctions[fn.Id] <- func


        return {| Plugins = plugins; SystemFunctions = systemFunctions |}
    }


    interface IFunctionService with
        member _.GetFunctions() = valueTask {
            let db = dbService.DbContext
            let! functionsInDb = db.Queryable<Function>().ToListAsync()
            return Seq.toList functionsInDb
        }

        member _.UpsertFunction(func) = valueTask {
            match func.Type with
            | FunctionType.Mcp config -> config.ClearToolsCache(func.Name, memoryCache)
            | _ -> ()

            let db = dbService.DbContext
            match! db.InsertOrUpdate<Function>().SetSource(func).ExecuteAffrowsAsync() with
            | 1 -> ()
            | _ -> failwith "Failed to upsert function"
        }

        member _.DeleteFunction(id) = valueTask {
            let db = dbService.DbContext
            db.Transaction(fun () ->
                db.Delete<Function>().Where(fun (x: Function) -> x.Id = id).ExecuteAffrows() |> ignore
                db.Delete<AgentFunction>().Where(fun (x: AgentFunction) -> x.Target = AgentFunctionTarget.Function id).ExecuteAffrows()
                |> ignore
            )
        }

        member _.UpdateUsedTime(id) = valueTask {
            let db = dbService.DbContext
            match! db.Update<Function>(id).Set((fun (x: Function) -> x.LastUsedAt), DateTime.Now).ExecuteAffrowsAsync() with
            | 1 -> ()
            | _ -> failwith "Failed to update function"
        }

        member _.GetKernelPlugins(ids, ?agentId, ?cancellationToken) = valueTask {
            let! result = this.GetKernelPluginsAndSystemFunctions(ids, ?agentId = agentId, ?cancellationToken = cancellationToken)

            let plugins = Collections.Generic.List<KernelPlugin>()

            result.Plugins |> Seq.iter (fun kv -> plugins.Add(kv.Value))

            plugins.AddFromFunctions(SystemFunction.PluginName, functions = (result.SystemFunctions |> Seq.map _.Value)) |> ignore

            return Seq.toList plugins
        }

        member _.CreateInvokeAgentFunc(author, agentId, loopId, sourceLoopContentId) =
            serviceProvider.GetRequiredService<SystemInvokeAgentFunc>().Create(author, agentId, loopId, sourceLoopContentId)
