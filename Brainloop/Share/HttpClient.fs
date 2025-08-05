[<AutoOpen>]
module Brainloop.Share.HttpClientExtensions

open System
open System.Net
open System.Net.Http
open System.Text.Json
open Fun.Result


type RequestOverrideHandler(?addiotionalBody: string) as this =
    inherit HttpClientHandler()

    member _.BaseSend(request: HttpRequestMessage, cancellationToken: System.Threading.CancellationToken) = base.SendAsync(request, cancellationToken)

    override _.SendAsync(request: HttpRequestMessage, cancellationToken: System.Threading.CancellationToken) = task {
        match addiotionalBody with
        | Some(SafeString addiotionalBody) ->
            let requestClone = new HttpRequestMessage(request.Method, request.RequestUri)
            for h in request.Headers do
                requestClone.Headers.Add(h.Key, h.Value)

            match request.Content with
            | null -> requestClone.Content <- new StringContent(addiotionalBody, mediaType = Headers.MediaTypeHeaderValue("application/json"))
            | content ->
                let! body = content.ReadAsStringAsync(cancellationToken = cancellationToken)
                let newContent = new StringContent(mergeJson body addiotionalBody)
                for h in content.Headers do
                    // Avoid overriding Content-Length and Content-Type headers
                    if h.Key <> "Content-Length" then
                        if h.Key = "Content-Type" then newContent.Headers.Remove(h.Key) |> ignore
                        newContent.Headers.Add(h.Key, h.Value)
                requestClone.Content <- newContent

            return! this.BaseSend(requestClone, cancellationToken)
        | _ -> return! this.BaseSend(request, cancellationToken)
    }


type HttpClient with

    static member Create(?headers: Map<string, string>, ?baseUrl: string, ?proxy: string, ?timeoutMs: int, ?addtionalRequestBody: string) =
        let httpClientHandler = new RequestOverrideHandler(?addiotionalBody = addtionalRequestBody)

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
