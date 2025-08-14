namespace Brainloop.Function.SystemFunctions

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open FSharp.Data
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

    let trim (x: string) = if String.IsNullOrEmpty(x) then "" else x.Trim()

    let convertHtmlToMarkdown (content) =
        let sb = StringBuilder()
        let doc = HtmlDocument.Parse(content)

        let rec loop (ele: HtmlNode) =
            for ele in ele.Elements() do
                let tag = ele.Name()
                let tag = if String.IsNullOrEmpty tag then "" else tag.ToLower()
                let isBlock =
                    match tag with
                    | "p"
                    | "div"
                    | "h1"
                    | "h2"
                    | "h3"
                    | "h4"
                    | "h5"
                    | "h6"
                    | "section"
                    | "main"
                    | "article"
                    | "aside"
                    | "head"
                    | "header"
                    | "footer"
                    | "nav"
                    | "blockquote"
                    | "ul"
                    | "ol"
                    | "pre" -> true
                    | _ -> false

                if isBlock then sb.AppendLine() |> ignore

                match tag with
                | x when String.IsNullOrEmpty(x) ->
                    let x = ele.InnerText() |> trim
                    if not (String.IsNullOrEmpty x) then sb.AppendLine(x) |> ignore
                | "script"
                | "style"
                | "link"
                | "meta" -> ()
                | "title"
                | "h1" -> sb.Append("# ").Append(ele.InnerText() |> trim).AppendLine() |> ignore
                | "h2" -> sb.Append("## ").Append(ele.InnerText() |> trim).AppendLine() |> ignore
                | "h3" -> sb.Append("### ").Append(ele.InnerText() |> trim).AppendLine() |> ignore
                | "h4" -> sb.Append("#### ").Append(ele.InnerText() |> trim).AppendLine() |> ignore
                | "h5" -> sb.Append("##### ").Append(ele.InnerText() |> trim).AppendLine() |> ignore
                | "h6" -> sb.Append("###### ").Append(ele.InnerText() |> trim).AppendLine() |> ignore
                | "a" ->
                    let href =
                        try
                            ele.Attribute("href").Value()
                        with _ ->
                            ""
                    let alt = ele.InnerText()
                    let alt = if String.IsNullOrEmpty(alt) then href else alt |> trim
                    sb.Append("[").Append(alt).Append("](").Append(href).Append(")") |> ignore
                | "img" ->
                    let src =
                        try
                            ele.Attribute("src").Value()
                        with _ ->
                            ""
                    let alt =
                        try
                            ele.Attribute("alt").Value()
                        with _ ->
                            ""
                    sb.Append("[").Append(alt).Append("](").Append(src).Append(")") |> ignore
                | "code" ->
                    let code = ele.InnerText() |> trim
                    if code.IndexOf "\n" <> code.LastIndexOf "\n" then
                        sb.AppendLine().AppendLine("```text ").AppendLine(code).AppendLine("```") |> ignore
                    else
                        sb.Append(" `").Append(code).Append("` ") |> ignore
                | "span" -> sb.Append(ele.InnerText() |> trim) |> ignore
                | "b" -> sb.Append("**").Append(ele.InnerText() |> trim).Append("**") |> ignore
                | "strong" -> sb.Append("**").Append(ele.InnerText() |> trim).Append("**") |> ignore
                | "em" -> sb.Append("*").Append(ele.InnerText() |> trim).Append("*") |> ignore
                | "br" -> sb.AppendLine() |> ignore
                | _ -> loop ele

                if isBlock then sb.AppendLine() |> ignore

        doc.Elements() |> Seq.iter loop
        sb.ToString().Trim()


    member _.Create(fn: Function, config: SystemSendHttpConfig) =
        KernelFunctionFactory.CreateFromMethod(
            Func<KernelArguments, ValueTask<SendHttpResult | null>>(fun args -> valueTask {
                try
                    let arguments = args.Get<SendHttpArgs>()
                    logger.LogInformation("Sending http request to {url}", arguments.Url)

                    use request =
                        new HttpRequestMessage(
                            HttpMethod(
                                match arguments.Method with
                                | null -> "GET"
                                | x -> x
                            ),
                            new Uri(arguments.Url)
                        )

                    match config.Headers with
                    | None -> ()
                    | Some headers ->
                        for KeyValue(key, value) in headers do
                            request.Headers.Add(key, value)

                    match arguments.Headers with
                    | null -> ()
                    | headers ->
                        for KeyValue(key, value) in headers do
                            request.Headers.Add(key, value)

                    match arguments.Body with
                    | null -> ()
                    | body -> request.Content <- new StringContent(body)

                    use http = HttpClient.Create(proxy = fn.Proxy)

                    let! response = http.SendAsync(request)
                    let! content = response.Content.ReadAsStringAsync()

                    let contentType = string response.Content.Headers.ContentType

                    let content =
                        match contentType with
                        | SafeStringStartWith "text/html" when config.ConvertHtmlToMarkdown -> convertHtmlToMarkdown content
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
            parameters = KernelParameterMetadata.FromInstance(SendHttpArgs()),
            loggerFactory = loggerFactory
        )
