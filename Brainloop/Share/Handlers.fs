namespace Brainloop.Share

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.SemanticKernel
open Brainloop.Db


type IStartChatLoopHandler =
    abstract member Handle:
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


type IGetTextFromImageHandler =
    abstract member Handle: imageFile: string * ?cancellationToken: CancellationToken -> ValueTask<string>


type IBuildTitleHandler =
    abstract member Handle: agentId: int * chatMessages: IList<ChatMessageContent> * ?cancellationToken: CancellationToken -> ValueTask<string>


type IRebuildMemoryHandler =
    abstract member Handle: ?cancellationToken: CancellationToken -> ValueTask<unit>


type IAddNotificationHandler =
    abstract member Handle: source: NotificationSource * message: string -> ValueTask<unit>


type IChatCompletionHandler =
    abstract member Handle:
        agentId: int *
        chatMessages: IList<ChatMessageContent> *
        targetContent: LoopContentWrapper *
        ?modelId: int *
        ?cancellationToken: CancellationToken ->
            ValueTask<unit>
