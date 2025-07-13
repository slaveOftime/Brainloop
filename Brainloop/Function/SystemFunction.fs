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
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.SemanticKernel
open Quartz
open IcedTasks
open FSharp.Control
open Fun.Result
open Brainloop.Db
open Brainloop.Model
open Brainloop.Memory
open Brainloop.Handler


[<RequireQualifiedAccess>]
module SystemFunction =
    [<Literal>]
    let PluginName = "system"

    [<Literal>]
    let GetCurrentTime = "get_current_time"

    [<Literal>]
    let RenderInIframe = "render_in_iframe"

    [<Literal>]
    let SendHttp = "send_http"

    [<Literal>]
    let SearchMemory = "search_memory"

    [<Literal>]
    let ReadDocumentAsText = "read_document_as_text"

    [<Literal>]
    let ExecuteCommand = "execute_command"

    [<Literal>]
    let GenerateImage = "generate_image"

    [<Literal>]
    let CreateTaskForAgent = "create_task_for_agent"

    [<Literal>]
    let CreateScheduledTaskForAgent = "create_scheduled_task_for_agent"


    let private isFunction (functionName: string) (name: string) =
        if name.StartsWith(PluginName) && name.Length > PluginName.Length + 2 then
            name.AsSpan().Slice(PluginName.Length + 1).SequenceEqual(functionName)
        else
            false

    let isRenderInIframe (name: string) = isFunction RenderInIframe name
    let isCreateTaskForAgent (name: string) = isFunction CreateTaskForAgent name


type SystemFunctionService
    (
        dbService: IDbService,
        documentService: IDocumentService,
        modelService: IModelService,
        logger: ILogger<SystemFunctionService>,
        schedulerFactory: ISchedulerFactory,
        serviceProvider: IServiceProvider,
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

    member private _.DownloadImageAsync(url: string, ?proxy) = valueTask {
        use httpClient = this.CreateBaseHttpClient(?proxy = proxy)
        let! response = httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
        return! response.Content.ReadAsStreamAsync()
    }

    member _.CreateSendHttpFunc(fn: Function, config: SystemSendHttpConfig) =
        KernelFunctionFactory.CreateFromMethod(
            Func<HttpRequestFunctionArgs, ValueTask<HttpRequestFunctionResult | null>>(fun args -> valueTask {
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

                    match args.Headers with
                    | null -> ()
                    | headers ->
                        for KeyValue(key, value) in headers do
                            request.Headers.Add(key, value)

                    match args.Body with
                    | null -> ()
                    | body -> request.Content <- new StringContent(body)

                    use http = this.CreateBaseHttpClient(proxy = fn.Proxy)

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

                    return HttpRequestFunctionResult(Status = response.StatusCode, ContentType = contentType, Content = content)

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

    member _.CreateExecuteCommandFunc(fn: Function, config: SystemExecuteCommandConfig, ?cancellationToken: CancellationToken) =
        KernelFunctionFactory.CreateFromMethod(
            Func<KernelArguments, ValueTask<string>>(fun args -> valueTask {
                try
                    logger.LogInformation("Executing command: {command} in {workingDir}", config.Command, config.WorkingDirectory)

                    let args = (args :> IEnumerable<KeyValuePair<string, obj | null>>).Select(fun x -> x.Key, string x.Value) |> Map.ofSeq

                    let mutable actualArguments = config.Arguments
                    for KeyValue(key, _) in config.ArgumentsDescription do
                        let value = args |> Map.tryFind key |> Option.defaultValue ""
                        actualArguments <- actualArguments.Replace("{{" + key + "}}", value)

                    let sb = StringBuilder()

                    let! result =
                        CliWrap.Cli
                            .Wrap(config.Command)
                            .WithValidation(CliWrap.CommandResultValidation.None)
                            .WithArguments(actualArguments)
                            .WithWorkingDirectory(config.WorkingDirectory)
                            .WithEnvironmentVariables(config.Environments)
                            .WithStandardErrorPipe(CliWrap.PipeTarget.ToDelegate(fun x -> sb.AppendLine x |> ignore))
                            .WithStandardOutputPipe(CliWrap.PipeTarget.ToDelegate(fun x -> sb.AppendLine x |> ignore))
                            .ExecuteAsync(?cancellationToken = cancellationToken)

                    if not result.IsSuccess then
                        logger.LogError("Command exited with code {exitCode}", result.ExitCode)

                    return sb.ToString()

                with ex ->
                    logger.LogError(ex, "Failed to execute command")
                    return string ex
            }),
            JsonSerializerOptions.createDefault (),
            functionName = fn.Name,
            description = fn.Description,
            loggerFactory = loggerFactory,
            parameters = [|
                for KeyValue(argName, argDescription) in config.ArgumentsDescription do
                    KernelParameterMetadata(
                        argName,
                        JsonSerializerOptions.createDefault (),
                        ParameterType = typeof<string>,
                        Description = argDescription
                    )
            |],
            returnParameter = KernelReturnParameterMetadata(Description = "The result of the command", ParameterType = typeof<string>)
        )


    member _.CreateGenerateImageFunc(fn: Function, config: SystemGenerateImageConfig, ?cancellationToken: CancellationToken) =
        KernelFunctionFactory.CreateFromMethod(
            Func<string, KernelArguments, ValueTask<string>>(fun prompt args -> valueTask {
                try
                    logger.LogInformation("Generate image")
                    match config with
                    | SystemGenerateImageConfig.LLMModel config ->
                        let! model = modelService.GetModelFromCache(config.ModelId)
                        let! kernel = modelService.GetKernel(config.ModelId)

                        let sourceLoopContentId =
                            match args.TryGetValue(Constants.ToolCallLoopContentId) with
                            | true, (:? int64 as x) -> Some x
                            | _ -> None

                        let textToImageService = kernel.GetRequiredService<TextToImage.ITextToImageService>()
                        let input = TextContent(prompt)
                        let executionSettings = PromptExecutionSettings()

                        let! images =
                            textToImageService.GetImageContentsAsync(
                                input,
                                executionSettings = executionSettings,
                                kernel = kernel,
                                ?cancellationToken = cancellationToken
                            )

                        let imageString = StringBuilder()

                        let addImageStream (notes: string) stream = valueTask {
                            let date = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")
                            let name = $"tool-generated-{date}.png"
                            let! id =
                                documentService.SaveFile(name, stream, ?loopContentId = sourceLoopContentId, ?cancellationToken = cancellationToken)
                            imageString.Append("![").Append(name).Append("](/api/memory/document/").Append(id).AppendLine("/image)") |> ignore
                            if String.IsNullOrWhiteSpace(notes) |> not then
                                imageString.AppendLine().AppendLine(notes) |> ignore
                        }

                        for image in images do
                            if image.Data.HasValue then
                                use stream = new MemoryStream(image.Data.Value.ToArray())
                                do! addImageStream prompt stream
                            else if image.DataUri <> null then
                                use! stream = this.DownloadImageAsync(image.DataUri, proxy = model.Proxy)
                                do! addImageStream prompt stream
                            else
                                match image.InnerContent with
                                | :? OpenAI.Images.GeneratedImage as image ->
                                    if image.ImageBytes <> null then
                                        use stream = new MemoryStream(image.ImageBytes.ToArray())
                                        do! addImageStream image.RevisedPrompt stream
                                    else if image.ImageUri <> null then
                                        use! stream = this.DownloadImageAsync(image.ImageUri.ToString(), proxy = model.Proxy)
                                        do! addImageStream image.RevisedPrompt stream
                                | _ -> ()

                        return imageString.ToString()

                with ex ->
                    logger.LogError(ex, "Failed to execute command")
                    raise ex
                    return ""
            }),
            JsonSerializerOptions.createDefault (),
            functionName = fn.Name,
            description = fn.Description,
            loggerFactory = loggerFactory,
            parameters = [|
                KernelParameterMetadata(
                    "prompt",
                    JsonSerializerOptions.createDefault (),
                    ParameterType = typeof<string>,
                    Description = "Prompt for generate the image"
                )
            |],
            returnParameter = KernelReturnParameterMetadata(Description = "Markdown string with image content", ParameterType = typeof<string>)
        )

    member _.CreateInvokeAgentFunc(author: string, agentId: int, loopId: int64, sourceLoopContentId: int64) =
        let agent = dbService.DbContext.Queryable<Agent>().Where(fun (x: Agent) -> x.Id = agentId).First<Agent>()
        let name = $"call_agent_{agent.Id}"
        KernelFunctionFactory.CreateFromMethod(
            Func<KernelArguments, CancellationToken, Task<unit>>(fun args ct -> task {
                logger.LogInformation("Call {agent} for help", agent.Name)

                let prompt =
                    match args.TryGetValue("prompt") with
                    | true, x -> string x
                    | _ -> "Help user based on the chat history"

                let callbackAfterFinish =
                    match args.TryGetValue("callbackAfterFinish") with
                    | true, (:? bool as x) -> x
                    | true, x when "true".Equals(string x, StringComparison.OrdinalIgnoreCase) -> true
                    | true, x when x = null || string x = "" -> true
                    | _ -> false

                let sourceLoopContentId =
                    match args.TryGetValue(Constants.ToolCallLoopContentId) with
                    | true, (:? int64 as x) -> x
                    | _ -> sourceLoopContentId

                let handler = serviceProvider.GetRequiredService<IStartChatLoopHandler>()

                valueTask {
                    do!
                        handler.Handle(
                            loopId,
                            prompt,
                            agentId = agent.Id,
                            author = author,
                            role = LoopContentAuthorRole.Agent,
                            ignoreInput = true,
                            sourceLoopContentId = sourceLoopContentId,
                            cancellationToken = ct
                        )

                    match args.TryGetValue(Constants.ToolCallAgentId) with
                    | true, (:? int as sourceAgentId) when callbackAfterFinish ->
                        logger.LogInformation("Send callback to agent #{targetAgentId} from agent {sourceAgentName}", sourceAgentId, agent.Name)
                        do!
                            handler.Handle(
                                loopId,
                                "",
                                agentId = sourceAgentId,
                                author = agent.Name,
                                role = LoopContentAuthorRole.Agent,
                                ignoreInput = true,
                                cancellationToken = ct
                            )
                    | _ -> ()

                }
                |> ignore
            }),
            JsonSerializerOptions.createDefault (),
            loggerFactory = loggerFactory,
            functionName = name,
            description = $"@{agent.Name} for help. AgentId={agent.Id}, AgentDescription={agent.Description}.",
            parameters = [|
                KernelParameterMetadata("prompt", Description = "Instruction for the agent", ParameterType = typeof<string>, IsRequired = true)
                KernelParameterMetadata(
                    "callbackAfterFinish",
                    Description = "If you want to get notification after the agent finish its task",
                    ParameterType = typeof<bool>,
                    DefaultValue = false,
                    IsRequired = false
                )
            |]
        )

    member _.CreateCreateTaskForAgentFunc(fn: Function) =
        KernelFunctionFactory.CreateFromMethod(
            Func<int64, string, ValueTask>(fun agentId prompt -> ValueTask.CompletedTask),
            JsonSerializerOptions.createDefault (),
            functionName = SystemFunction.CreateTaskForAgent,
            description = fn.Name + " " + fn.Description,
            loggerFactory = loggerFactory,
            parameters = [|
                KernelParameterMetadata("agentId", JsonSerializerOptions.createDefault (), ParameterType = typeof<int>, Description = "AgentId")
                KernelParameterMetadata(
                    "prompt",
                    JsonSerializerOptions.createDefault (),
                    ParameterType = typeof<string>,
                    Description = "Prompt for the agent"
                )
            |]
        )

    member _.CreateCreateScheduledTaskForAgentFunc(fn: Function, ?cancellationToken: CancellationToken) =
        KernelFunctionFactory.CreateFromMethod(
            Func<int, string, string, string, KernelArguments, ValueTask<unit>>(fun agentId schedulerIdentity cronExpression prompt args -> valueTask {
                try
                    match args.TryGetValue(Constants.ToolCallLoopId) with
                    | true, (:? int64 as loopId) ->
                        let! scheduler = schedulerFactory.GetScheduler()

                        let sourceAgentId =
                            match args.TryGetValue(Constants.ToolCallAgentId) with
                            | true, (:? int as x) -> x
                            | _ -> agentId

                        let agentName =
                            dbService.DbContext.Queryable<Agent>().Where(fun (x: Agent) -> x.Id = sourceAgentId).First(fun (x: Agent) -> x.Name)

                        let data = {
                            Identity = schedulerIdentity
                            Author = agentName
                            AgentId = agentId
                            LoopId = loopId
                            CronExpression = cronExpression
                            Prompt = prompt
                        }

                        let trigger =
                            TriggerBuilder
                                .Create()
                                .WithIdentity(schedulerIdentity, Constants.SchedulerGroupForAgent)
                                .WithCronSchedule(data.CronExpression)
                                .StartNow()
                                .Build()

                        let job =
                            JobBuilder
                                .Create<SystemScheduledTaskToCallAgentJob>()
                                .WithIdentity(schedulerIdentity, Constants.SchedulerGroupForAgent)
                                .UsingJobData("data", JsonSerializer.Serialize(data, JsonSerializerOptions.createDefault ()))
                                .Build()

                        do! scheduler.ScheduleJob(job, trigger, ?cancellationToken = cancellationToken) |> Task.map ignore

                        let addNotificationHandler = serviceProvider.GetRequiredService<IAddNotificationHandler>()
                        do!
                            addNotificationHandler.Handle(
                                NotificationSource.Scheduler {
                                    Name = schedulerIdentity
                                    Group = Constants.SchedulerGroupForAgent
                                    Author = data.Author
                                    AgentId = data.AgentId
                                    LoopId = data.LoopId
                                },
                                $"Scheduled {schedulerIdentity}"
                            )

                    | _ ->
                        logger.LogWarning("No loopId found in the arguments for scheduling task for agent {agentId}", agentId)
                        raise (ArgumentException("No loopId found in the arguments for scheduling task"))

                with ex ->
                    logger.LogError(ex, "Failed to schedule task for agent {agentId}", agentId)
                    raise ex
            }),
            JsonSerializerOptions.createDefault (),
            functionName = SystemFunction.CreateScheduledTaskForAgent,
            description = fn.Name + " " + fn.Description,
            loggerFactory = loggerFactory,
            parameters = [|
                KernelParameterMetadata("agentId", JsonSerializerOptions.createDefault (), ParameterType = typeof<int>, Description = "AgentId")
                KernelParameterMetadata(
                    "schedulerIdentity",
                    JsonSerializerOptions.createDefault (),
                    ParameterType = typeof<string>,
                    Description = "Scheduler identity which can be used to delete a scheduler in the future if needed"
                )
                KernelParameterMetadata(
                    "cronExpression",
                    JsonSerializerOptions.createDefault (),
                    ParameterType = typeof<string>,
                    Description =
                        "CRON expression for the scheduler. Support for specifying both a day-of-week and a day-of-month value is not complete (you must currently use the ? character in one of these fields)."
                )
                KernelParameterMetadata(
                    "prompt",
                    JsonSerializerOptions.createDefault (),
                    ParameterType = typeof<string>,
                    Description = "Prompt for the agent"
                )
            |]
        )
