[<AutoOpen>]
module Brainloop.Function.Utils

open System
open Brainloop.Db


type String with
    member str.ToAscii() = str |> Seq.map (fun x -> if Char.IsDigit x || Char.IsLetter x then x else '_') |> Seq.toArray |> String


let (|SYSTEM_FUNCTION|_|) (ty: FunctionType) =
    match ty with
    | FunctionType.SystemGetCurrentTime
    | FunctionType.SystemRenderInIframe
    | FunctionType.SystemSendHttp _
    | FunctionType.SystemSearchMemory _
    | FunctionType.SystemReadDocumentAsText
    | FunctionType.SystemExecuteCommand _
    | FunctionType.SystemGenerateImage _
    | FunctionType.SystemCreateTaskForAgent
    | FunctionType.SystemCreateScheduledTaskForAgent -> true
    | FunctionType.Mcp _
    | FunctionType.OpenApi _
    | FunctionType.OpenApiUrl _ -> false
