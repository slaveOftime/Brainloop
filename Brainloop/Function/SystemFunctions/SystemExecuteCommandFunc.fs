namespace Brainloop.Function.SystemFunctions

open System
open System.Linq
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open IcedTasks
open Brainloop.Db


type SystemExecuteCommandFunc(logger: ILogger<SystemExecuteCommandFunc>, loggerFactory: ILoggerFactory) =

    [<Literal>]
    let WorkingDirectoryArgKey = "workingtDirectory"

    member _.Create(fn: Function, config: SystemExecuteCommandConfig, ?cancellationToken: CancellationToken) =
        KernelFunctionFactory.CreateFromMethod(
            Func<KernelArguments, ValueTask<string>>(fun args -> valueTask {
                try
                    logger.LogInformation("Executing command: {command} in {workingDir}", config.Command, config.WorkingDirectory)

                    let args = (args :> IEnumerable<KeyValuePair<string, obj | null>>).Select(fun x -> x.Key, string x.Value) |> Map.ofSeq

                    let workingDirectory =
                        match config.SupportOverrideWorkingDirectory with
                        | ValueSome true -> args |> Map.tryFind WorkingDirectoryArgKey |> Option.defaultValue config.WorkingDirectory
                        | _ -> config.WorkingDirectory

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
                            .WithWorkingDirectory(workingDirectory)
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
                match config.SupportOverrideWorkingDirectory with
                | ValueSome true ->
                    KernelParameterMetadata(
                        WorkingDirectoryArgKey,
                        JsonSerializerOptions.createDefault (),
                        ParameterType = typeof<string>,
                        IsRequired = false,
                        Description =
                            $"The working directory where the command will be executed, this is not required. If it is not provided, the default one will be used: {config.WorkingDirectory}"
                    )
                | _ -> ()
            |],
            returnParameter = KernelReturnParameterMetadata(Description = "The result of the command", ParameterType = typeof<string>)
        )
