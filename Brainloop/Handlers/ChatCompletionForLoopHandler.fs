namespace Brainloop.Handlers

open Brainloop.Share
open Brainloop.Loop


type ChatCompletionForLoopHandler(loopService: ILoopService) =

    interface IChatCompletionForLoopHandler with
        member _.Handle
            (loopId, message, ?agentId, ?modelId, ?author, ?role, ?includeHistory, ?ignoreInput, ?sourceLoopContentId, ?cancellationToken)
            =
            loopService.Send(
                loopId,
                message,
                ?agentId = agentId,
                ?modelId = modelId,
                ?author = author,
                ?role = role,
                ?includeHistory = includeHistory,
                ?ignoreInput = ignoreInput,
                ?sourceLoopContentId = sourceLoopContentId,
                ?cancellationToken = cancellationToken
            )
