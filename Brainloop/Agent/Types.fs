namespace Brainloop.Agent

open System.Threading.Tasks
open Brainloop.Db


type IAgentService =
    abstract member GetAgents: unit -> ValueTask<Agent list>
    abstract member GetAgentsWithCache: unit -> ValueTask<Agent list>
    abstract member TryGetAgentWithCache: id: int -> ValueTask<Agent voption>

    abstract member GetTitleBuilderAgent: unit -> ValueTask<Agent | null>
    
    abstract member UpsertAgent: agent: Agent -> ValueTask<unit>
    abstract member DeleteAgent: id: int -> ValueTask<unit>
    abstract member UpdateUsedTime: id: int -> ValueTask<unit>

    abstract member IsFunctionInWhitelist: agentId: int * functionName: string -> ValueTask<bool>
    abstract member AddFunctionIntoWhitelist: agentId: int * functionName: string -> ValueTask<unit>
    abstract member RemoveFunctionFromWhitelist: agentId: int * functionName: string -> ValueTask<unit>

    abstract member AddFunctionIntoSet: agentId: int * target: AgentFunctionTarget -> ValueTask<unit>
    abstract member RemoveFunctionFromSet: agentId: int * target: AgentFunctionTarget -> ValueTask<unit>
