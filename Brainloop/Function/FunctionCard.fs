namespace Brainloop.Function

open System.Text.RegularExpressions
open FSharp.Data.Adaptive
open MudBlazor
open Fun.Blazor
open Brainloop.Db
open Brainloop.Model


type FunctionCard =

    static member Create(functionForm: AdaptiveForm<Function, string>) = MudGrid'' {
        Spacing 4
        MudItem'' {
            xs 12
            adapt {
                let! binding = functionForm.UseFieldWithErrors(fun x -> x.Name)
                MudTextField'' {
                    Value' binding
                    Label "Name"
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! binding = functionForm.UseField(fun x -> x.Description)
                MudTextField'' {
                    Value' binding
                    Label "Description"
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! v, setV = functionForm.UseField(fun x -> x.Type)
                MudSelect'' {
                    Value v
                    ValueChanged(fun v ->
                        let setName = functionForm.UseFieldSetter(fun x -> x.Name)
                        let setDescription = functionForm.UseFieldSetter(fun x -> x.Description)
                        match v with
                        | FunctionType.SystemGetCurrentTime ->
                            setName SystemFunction.GetCurrentTime
                            setDescription "Get current time"
                        | FunctionType.SystemRenderInIframe ->
                            setName SystemFunction.RenderInIframe
                            setDescription "Render html string in iframe"
                        | FunctionType.SystemSendHttp _ ->
                            setName SystemFunction.SendHttp
                            setDescription "Send http request"
                        | FunctionType.SystemSearchMemory _ ->
                            setName SystemFunction.SearchMemory
                            setDescription "Search memory by natural language"
                        | FunctionType.SystemReadDocumentAsText ->
                            setName SystemFunction.ReadDocumentAsText
                            setDescription "Read uploaded document as text"
                        | FunctionType.SystemExecuteCommand _ -> setDescription "Execute command"
                        | FunctionType.SystemCreateTaskForAgent ->
                            setName SystemFunction.CreateTaskForAgent
                            setDescription "Create a task for a specific agent"
                        | FunctionType.SystemCreateScheduledTaskForAgent ->
                            setName SystemFunction.CreateScheduledTaskForAgent
                            setDescription "Create a task scheduler for a specific agent"
                        | _ -> ()
                        setV v
                    )
                    Label "Function Type"
                    ToStringFunc(
                        function
                        | FunctionType.Mcp(McpConfig.STDIO _) -> "MCP STDIO"
                        | FunctionType.Mcp(McpConfig.SSE _) -> "MCP SSE"
                        | FunctionType.OpenApi _ -> "OpenApi with json schema"
                        | FunctionType.OpenApiUrl _ -> "OpenApi with json url"
                        | FunctionType.SystemGetCurrentTime -> "System: get current time"
                        | FunctionType.SystemRenderInIframe -> "System: render in iframe"
                        | FunctionType.SystemSendHttp _ -> "System: send http request"
                        | FunctionType.SystemSearchMemory _ -> "System: search memory"
                        | FunctionType.SystemReadDocumentAsText -> "System: read document as text"
                        | FunctionType.SystemExecuteCommand _ -> "System: execute command"
                        | FunctionType.SystemGenerateImage _ -> "System: generate image"
                        | FunctionType.SystemCreateTaskForAgent -> "System: create task for agent"
                        | FunctionType.SystemCreateScheduledTaskForAgent -> "System: create scheduled task for agent"
                    )
                    for option in
                        [
                            FunctionType.SystemGetCurrentTime
                            FunctionType.SystemRenderInIframe
                            FunctionType.SystemSendHttp SystemSendHttpConfig.Default
                            FunctionType.SystemSearchMemory SystemSearchMemoryConfig.Default
                            FunctionType.SystemReadDocumentAsText
                            FunctionType.SystemExecuteCommand SystemExecuteCommandConfig.Default
                            FunctionType.SystemGenerateImage SystemGenerateImageConfig.Default
                            FunctionType.SystemCreateTaskForAgent
                            FunctionType.SystemCreateScheduledTaskForAgent
                            FunctionType.Mcp McpConfig.Default
                            FunctionType.Mcp McpConfig.DefaultSSE
                            FunctionType.OpenApi OpenApiConfig.Default
                            FunctionType.OpenApiUrl OpenApiUriConfig.Default
                        ] do
                        MudSelectItem'' { Value option }
                }
            }
        }
        MudItem'' {
            xs 12
            adapt {
                let! functionType = functionForm.UseFieldValue(fun x -> x.Type)
                let isProxySupported =
                    match functionType with
                    | FunctionType.Mcp(McpConfig.SSE _) -> true
                    | FunctionType.Mcp(McpConfig.STDIO _) -> false
                    | FunctionType.OpenApi _
                    | FunctionType.OpenApiUrl _
                    | FunctionType.SystemSendHttp _ -> true
                    | FunctionType.SystemGetCurrentTime
                    | FunctionType.SystemRenderInIframe
                    | FunctionType.SystemSearchMemory _
                    | FunctionType.SystemReadDocumentAsText
                    | FunctionType.SystemExecuteCommand _
                    | FunctionType.SystemGenerateImage _
                    | FunctionType.SystemCreateTaskForAgent
                    | FunctionType.SystemCreateScheduledTaskForAgent -> false
                if isProxySupported then
                    let! binding = functionForm.UseField(fun x -> x.Proxy)
                    MudTextField'' {
                        Value' binding
                        Label "Proxy"
                    }
            }
        }
        MudItem'' {
            xs 12
            div {
                style {
                    displayFlex
                    flexDirectionColumn
                    gap 16
                }
                adapt {
                    let! functionName = functionForm.UseFieldValue(fun x -> x.Name)
                    let! functionType, setFunctionType = functionForm.UseField(fun x -> x.Type)
                    match functionType with
                    | FunctionType.SystemGetCurrentTime
                    | FunctionType.SystemRenderInIframe
                    | FunctionType.SystemReadDocumentAsText
                    | FunctionType.SystemCreateTaskForAgent
                    | FunctionType.SystemCreateScheduledTaskForAgent -> ()

                    | FunctionType.SystemSendHttp config -> MudSwitch'' {
                        Color(if config.ConvertHtmlToMarkdown then Color.Primary else Color.Default)
                        Value config.ConvertHtmlToMarkdown
                        ValueChanged(fun x -> { config with ConvertHtmlToMarkdown = x } |> FunctionType.SystemSendHttp |> setFunctionType)
                        Label "Convert text (html) response to markdown"
                      }

                    | FunctionType.SystemSearchMemory config -> MudTextField'' {
                        Value config.Top
                        ValueChanged(fun x -> { config with Top = x } |> FunctionType.SystemSearchMemory |> setFunctionType)
                        Label "Top results to take"
                        AutoFocus
                      }

                    | FunctionType.SystemExecuteCommand config ->
                        let argumentsDescription =
                            Regex.Matches(config.Arguments, "{{(.*?)}}")
                            |> Seq.map (fun x ->
                                let argName = x.Groups[1].Value
                                let argDescription = config.ArgumentsDescription |> Map.tryFind argName |> Option.defaultValue ""
                                argName, argDescription
                            )
                            |> Map.ofSeq
                        MudTextField'' {
                            Value config.Command
                            ValueChanged(fun x -> { config with Command = x } |> FunctionType.SystemExecuteCommand |> setFunctionType)
                            Label "Command"
                            AutoFocus
                        }
                        MudTextField'' {
                            Value config.Arguments
                            ValueChanged(fun x -> { config with Arguments = x } |> FunctionType.SystemExecuteCommand |> setFunctionType)
                            Label "Arguments"
                            AutoGrow
                            Lines 1
                            MaxLines 10
                        }
                        MudField'' {
                            Label "Arguments Description"
                            Variant Variant.Outlined
                            KeyValueField.Create(
                                argumentsDescription,
                                (fun x -> { config with ArgumentsDescription = x } |> FunctionType.SystemExecuteCommand |> setFunctionType),
                                readOnlyKey = true,
                                disableCreateNew = true
                            )
                        }
                        MudTextField'' {
                            Value config.WorkingDirectory
                            ValueChanged(fun x -> { config with WorkingDirectory = x } |> FunctionType.SystemExecuteCommand |> setFunctionType)
                            Label "Working Directory"
                        }
                        MudField'' {
                            Label "Enviroments"
                            Variant Variant.Outlined
                            KeyValueField.Create(
                                config.Environments,
                                fun x -> { config with Environments = x } |> FunctionType.SystemExecuteCommand |> setFunctionType
                            )
                        }

                    | FunctionType.SystemGenerateImage(SystemGenerateImageConfig.LLMModel config) ->
                        ModelSelector.Create(
                            AVal.constant (
                                config.ModelId,
                                (fun x ->
                                    { config with ModelId = x }
                                    |> SystemGenerateImageConfig.LLMModel
                                    |> FunctionType.SystemGenerateImage
                                    |> functionForm.UseFieldSetter(fun x -> x.Type)
                                )
                            ),
                            filter = fun x -> x.CanHandleImage
                        )

                    | FunctionType.OpenApi config ->
                        MudTextField'' {
                            Value config.JsonSchema
                            ValueChanged(fun x -> { config with JsonSchema = x } |> FunctionType.OpenApi |> setFunctionType)
                            Label "OpenApi json schema"
                            Variant Variant.Outlined
                            Lines 5
                            MaxLines 30
                            AutoGrow
                        }
                        MudField'' {
                            Label "Headers"
                            Variant Variant.Outlined
                            KeyValueField.Create(config.Headers, fun x -> { config with Headers = x } |> FunctionType.OpenApi |> setFunctionType)
                        }

                    | FunctionType.OpenApiUrl config ->
                        MudTextField'' {
                            Value config.Url
                            ValueChanged(fun x -> { config with Url = x } |> FunctionType.OpenApiUrl |> setFunctionType)
                            Label "OpenApi url"
                            Variant Variant.Outlined
                        }
                        MudField'' {
                            Label "Headers"
                            Variant Variant.Outlined
                            KeyValueField.Create(config.Headers, fun x -> { config with Headers = x } |> FunctionType.OpenApiUrl |> setFunctionType)
                        }

                    | FunctionType.Mcp functionConfig ->
                        match functionConfig with
                        | McpConfig.STDIO config ->
                            MudTextField'' {
                                Value config.Command
                                ValueChanged(fun x -> { config with Command = x } |> McpConfig.STDIO |> FunctionType.Mcp |> setFunctionType)
                                Label "Command"
                            }
                            MudTextField'' {
                                Value config.Arguments
                                ValueChanged(fun x -> { config with Arguments = x } |> McpConfig.STDIO |> FunctionType.Mcp |> setFunctionType)
                                Label "Arguments"
                            }
                            MudTextField'' {
                                Value config.WorkingDirectory
                                ValueChanged(fun x -> { config with WorkingDirectory = x } |> McpConfig.STDIO |> FunctionType.Mcp |> setFunctionType)
                                Label "Working Directory"
                            }
                            MudField'' {
                                Label "Enviroments"
                                Variant Variant.Outlined
                                KeyValueField.Create(
                                    config.Environments,
                                    fun x -> { config with Environments = x } |> McpConfig.STDIO |> FunctionType.Mcp |> setFunctionType
                                )
                            }

                        | McpConfig.SSE config ->
                            MudTextField'' {
                                Value config.Url
                                ValueChanged(fun x -> { config with Url = x } |> McpConfig.SSE |> FunctionType.Mcp |> setFunctionType)
                                Label "Url"
                            }
                            MudTextField'' {
                                Value config.Authorization
                                ValueChanged(fun x -> { config with Authorization = x } |> McpConfig.SSE |> FunctionType.Mcp |> setFunctionType)
                                Label "Authorization (Bearer)"
                            }
                            MudField'' {
                                Label "Headers"
                                Variant Variant.Outlined
                                KeyValueField.Create(
                                    config.Headers,
                                    fun x -> { config with Headers = x } |> McpConfig.SSE |> FunctionType.Mcp |> setFunctionType
                                )
                            }
                        McpToolsChecker.Create(functionName, functionConfig)
                }
            }
        }
    }
