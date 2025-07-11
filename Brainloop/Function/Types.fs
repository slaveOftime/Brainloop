namespace Brainloop.Function

open System.Net
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.SemanticKernel
open Brainloop.Db


// Canot use fsharp record type because json schema generation
type HttpRequestFunctionArgs() =
    member val Url: string = "" with get, set
    member val Method: string | null = null with get, set
    member val Headers: Dictionary<string, string> | null = null with get, set
    member val Body: string | null = null with get, set

type HttpRequestFunctionResult() =
    member val Status: HttpStatusCode = HttpStatusCode.OK with get, set
    member val ContentType: string = "text" with get, set
    member val Content: string = "" with get, set


type IFunctionService =
    abstract member GetFunctions: unit -> ValueTask<Function list>
    abstract member UpsertFunction: func: Function -> ValueTask<unit>
    abstract member DeleteFunction: id: int -> ValueTask<unit>
    abstract member UpdateUsedTime: id: int -> ValueTask<unit>

    abstract member GetKernelPlugins: ids: int seq * ?cancellationToken: CancellationToken -> ValueTask<KernelPlugin list>

    abstract member CreateInvokeAgentFunc: author: string * agentId: int * loopId: int64 * sourceLoopContentId: int64 -> KernelFunction
