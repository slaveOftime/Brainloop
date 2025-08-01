namespace Brainloop.Function

open System.Threading
open System.Threading.Tasks
open Microsoft.SemanticKernel
open Brainloop.Db


type IFunctionService =
    abstract member GetFunctions: unit -> ValueTask<Function list>
    abstract member UpsertFunction: func: Function -> ValueTask<unit>
    abstract member DeleteFunction: id: int -> ValueTask<unit>
    abstract member UpdateUsedTime: id: int -> ValueTask<unit>

    abstract member GetKernelPlugins: ids: int seq * ?agentId: int * ?cancellationToken: CancellationToken -> ValueTask<KernelPlugin list>

    abstract member CreateInvokeAgentFunc: author: string * agentId: int -> KernelFunction
