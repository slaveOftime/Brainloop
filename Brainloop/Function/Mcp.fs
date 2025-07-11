[<AutoOpen>]
module Brainloop.Function.McpUtils

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Collections.Generic
open System.Threading
open FSharp.Control
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Caching.Memory
open ModelContextProtocol.Client
open Fun.Result
open Brainloop.Db


type McpConfig with
    member config.GetClient(?loggerFactory: ILoggerFactory, ?httpClient: HttpClient, ?cancellationToken: CancellationToken) = task {
        let clientTransport: IClientTransport =
            match config with
            | McpConfig.SSE config ->
                let httpClient = httpClient |> Option.defaultWith (fun _ -> new HttpClient())
                if not (String.IsNullOrEmpty config.Authorization) then
                    httpClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", config.Authorization)
                for KeyValue(k, v) in config.Headers do
                    httpClient.DefaultRequestHeaders.Add(k, v)
                let options = SseClientTransportOptions(Endpoint = Uri(config.Url), ConnectionTimeout = TimeSpan.FromMinutes(3L))
                SseClientTransport(options, httpClient, ?loggerFactory = loggerFactory) :> IClientTransport

            | McpConfig.STDIO config ->
                let options =
                    StdioClientTransportOptions(
                        Command = config.Command,
                        Arguments = (System.CommandLine.Parsing.CommandLineParser.SplitCommandLine(config.Arguments) |> Seq.toArray),
                        EnvironmentVariables = Dictionary config.Environments,
                        WorkingDirectory =
                            match config.WorkingDirectory with
                            | NullOrEmptyString -> Directory.GetCurrentDirectory()
                            | x -> x
                    )
                StdioClientTransport(options, ?loggerFactory = loggerFactory) :> IClientTransport

        return! McpClientFactory.CreateAsync(clientTransport, ?loggerFactory = loggerFactory, ?cancellationToken = cancellationToken)
    }

    member config.GetTools
        (name: string, memoryCache: IMemoryCache, ?loggerFactory: ILoggerFactory, ?httpClient: HttpClient, ?cancellationToken: CancellationToken)
        =
        memoryCache.GetOrCreateAsync(
            $"mcp-tools-{name}",
            (fun _ -> task {
                let! client = config.GetClient(?loggerFactory = loggerFactory, ?httpClient = httpClient, ?cancellationToken = cancellationToken)
                let! tools = client.ListToolsAsync(?cancellationToken = cancellationToken)
                return tools |> Seq.toList
            }),
            MemoryCacheEntryOptions(SlidingExpiration = TimeSpan.FromMinutes(5L))
        )
        |> Task.map (
            function
            | null -> []
            | x -> x
        )

    member _.ClearToolsCache(name: string, memoryCache: IMemoryCache) = memoryCache.Remove($"mcp-tools-{name}")
