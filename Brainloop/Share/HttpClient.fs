[<AutoOpen>]
module Brainloop.Share.HttpClientExtensions

open System
open System.Net
open System.Net.Http
open Fun.Result


type HttpClient with

    static member Create(?headers: Map<string, string>, ?baseUrl: string, ?proxy: string, ?timeoutMs: int) =
        let httpClientHandler = new HttpClientHandler()

        match proxy with
        | Some(SafeString proxy) ->
            let webProxy = WebProxy(Uri(proxy))
            httpClientHandler.UseProxy <- true
            httpClientHandler.Proxy <- webProxy
        | _ -> ()

        let httpClient = new HttpClient(httpClientHandler)

        httpClient.Timeout <- TimeSpan.FromMilliseconds(int64 (defaultArg timeoutMs 600_000))

        match baseUrl with
        | Some(SafeString baseUrl) -> httpClient.BaseAddress <- Uri(if baseUrl.EndsWith "/" then baseUrl else baseUrl + "/")
        | _ -> ()

        match headers with
        | Some headers ->
            for KeyValue(key, value) in headers do
                httpClient.DefaultRequestHeaders.Add(key, value)
        | _ -> ()

        httpClient
