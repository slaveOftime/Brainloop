namespace Brainloop.Function.SystemFunctions

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open IcedTasks
open Fun.Result
open Brainloop.Db
open Brainloop.Share
open Brainloop.Function


type SendHttpArgs() =
    member val Url: string = "" with get, set
    member val Method: string | null = null with get, set
    member val Headers: Dictionary<string, string> | null = null with get, set
    member val Body: string | null = null with get, set

type SendHttpResult() =
    member val Status: HttpStatusCode = HttpStatusCode.OK with get, set
    member val ContentType: string = "text" with get, set
    member val Content: string = "" with get, set


type SystemSendHttpFunc(logger: ILogger<SystemSendHttpFunc>, loggerFactory: ILoggerFactory) =

    member _.Create(fn: Function, config: SystemSendHttpConfig) =
        KernelFunctionFactory.CreateFromMethod(
            Func<SendHttpArgs, ValueTask<SendHttpResult | null>>(fun args -> valueTask {
                try
                    logger.LogInformation("Sending http request to {url}", args.Url)

                    use request =
                        new HttpRequestMessage(
                            HttpMethod(
                                match args.Method with
                                | null -> "GET"
                                | x -> x
                            ),
                            new Uri(args.Url)
                        )

                    match config.Headers with
                    | None -> ()
                    | Some headers ->
                        for KeyValue(key, value) in headers do
                            request.Headers.Add(key, value)

                    match args.Headers with
                    | null -> ()
                    | headers ->
                        for KeyValue(key, value) in headers do
                            request.Headers.Add(key, value)

                    match args.Body with
                    | null -> ()
                    | body -> request.Content <- new StringContent(body)

                    use http = HttpClient.Create(proxy = fn.Proxy)

                    let! response = http.SendAsync(request)
                    let! content = response.Content.ReadAsStringAsync()

                    let contentType = string response.Content.Headers.ContentType

                    let content =
                        match contentType with
                        | SafeStringStartWith "text/html" when config.ConvertHtmlToMarkdown ->
                            ReverseMarkdown
                                .Converter(
                                    ReverseMarkdown.Config(
                                        CleanupUnnecessarySpaces = true,
                                        GithubFlavored = true,
                                        RemoveComments = true,
                                        SmartHrefHandling = true,
                                        UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.PassThrough
                                    )
                                )
                                .Convert(content)
                        | _ -> content

                    return SendHttpResult(Status = response.StatusCode, ContentType = contentType, Content = content)

                with ex ->
                    logger.LogError(ex, "Failed to send http request")
                    raise ex
                    return null
            }),
            JsonSerializerOptions.createDefault (),
            functionName = SystemFunction.SendHttp,
            description = fn.Name + " " + fn.Description,
            loggerFactory = loggerFactory
        )
