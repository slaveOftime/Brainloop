namespace Brainloop.Entry

open FSharp.Data.Adaptive
open MudBlazor
open Fun.Blazor
open Brainloop.Db
open Brainloop.Model
open Brainloop.Agent
open Brainloop.Function


type WelcomeView =

    static member private WarningWithLink(link, msg: NodeRenderFragment) = div {
        style {
            displayFlex
            flexDirectionColumn
            justifyContentCenter
            alignContentCenter
            gap 20
            backgroundColor "var(--mud-palette-background)"
            positionAbsolute
            top 0
            left 0
            right 0
            height "100%"
        }
        MudText'' {
            Color Color.Primary
            Typo Typo.h5
            Align Align.Center
            "Welcome to Brainloop!"
        }
        MudLink'' {
            Href link
            Underline Underline.Always
            MudText'' {
                Color Color.Primary
                Typo Typo.h6
                Align Align.Center
                msg
            }
        }
        MudLink'' {
            Href "/"
            Underline Underline.Always
            MudText'' {
                Color Color.Primary
                Align Align.Center
                "Refresh if you already setup"
            }
        }
    }

    static member Create(isModelAndAgentsReady: bool, setIsModelAndAgentsReady: bool -> unit) =
        html.inject (fun (modelService: IModelService, agentService: IAgentService, functionService: IFunctionService) -> task {
            let! functions = functionService.GetFunctions()
            if functions.IsEmpty then
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.GetCurrentTime
                                Description = "Get current time"
                                Type = FunctionType.SystemGetCurrentTime
                        }
                    )
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.RenderInIframe
                                Description = "Render content in an iframe"
                                Type = FunctionType.SystemRenderInIframe
                        }
                    )
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.SendHttp
                                Description = "Send HTTP request to a URL"
                                Type = FunctionType.SystemSendHttp SystemSendHttpConfig.Default
                        }
                    )
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.SearchMemory
                                Description = "Search memory with query"
                                Type = FunctionType.SystemSearchMemory SystemSearchMemoryConfig.Default
                        }
                    )
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.ReadDocumentAsText
                                Description = "Read document as text"
                                Type = FunctionType.SystemReadDocumentAsText
                        }
                    )
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.CreateTaskForAgent
                                Description = "Create a task for an agent"
                                Type = FunctionType.SystemCreateTaskForAgent
                        }
                    )
                do!
                    functionService.UpsertFunction(
                        {
                            Function.Default with
                                Name = SystemFunction.CreateScheduledTaskForAgent
                                Description = "Create a scheduled task for an agent"
                                Type = FunctionType.SystemCreateScheduledTaskForAgent
                        }
                    )


            if isModelAndAgentsReady then
                return html.none
            else
                let! models = modelService.GetModelsWithCache()
                if models.IsEmpty then
                    return WelcomeView.WarningWithLink("/models", span { "Please create some models then define an agent to use it." })
                else
                    let! agents = agentService.GetAgentsWithCache()
                    if agents.IsEmpty then
                        return WelcomeView.WarningWithLink("/agents", span { "Please create some agents to use your models." })
                    else
                        setIsModelAndAgentsReady true
                        // Put the loops under the bottom always, so it will not be rendered on navigation
                        return html.none
        })
