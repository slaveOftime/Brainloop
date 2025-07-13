namespace Brainloop.Loop

open System.Threading
open System.Threading.Tasks
open FSharp.Data.Adaptive
open Brainloop.Db
open Brainloop.Share


type ILoopContentService =
    abstract member GetOrCreateContentsCache: loopId: int64 -> ValueTask<ChangeableIndexList<LoopContentWrapper>>
    abstract member GetContentsCache: loopId: int64 -> ChangeableIndexList<LoopContentWrapper> | null
    abstract member RemoveFromCache: loopId: int64 -> unit

    abstract member LoadMoreContentsIntoCache: loopId: int64 -> ValueTask<unit>
    abstract member LoadMoreLatestContentsIntoCache: loopId: int64 -> ValueTask<unit>

    abstract member AddContentToCacheAndUpsert: content: LoopContentWrapper -> ValueTask<int64>

    abstract member UpsertLoopContent: content: LoopContentWrapper * ?disableVectorize: bool -> ValueTask<int64>
    abstract member UpdateLoopContent: loopId: int64 * loopContentId: int64 * content: string -> ValueTask<unit>
    abstract member DeleteLoopContent: loopId: int64 * loopContentId: int64 -> ValueTask<unit>
    abstract member DeleteLoopContentsOfSource: loopId: int64 * loopContentId: int64 -> ValueTask<unit>


type ILoopService =
    abstract member Send:
        loopId: int64 *
        message: string *
        ?agentId: int *
        ?modelId: int *
        ?author: string *
        ?role: LoopContentAuthorRole *
        ?includeHistory: bool *
        ?ignoreInput: bool *
        ?sourceLoopContentId: int64 *
        ?cancellationToken: CancellationToken ->
            ValueTask<unit>

    abstract member Resend: loopId: int64 * toMessageId: int64 * ?modelId: int -> ValueTask<unit>

    abstract member BuildTitle: loopId: int64 * ?title: string -> ValueTask<unit>

    abstract member IsStreaming: loopId: int64 -> aval<bool>
