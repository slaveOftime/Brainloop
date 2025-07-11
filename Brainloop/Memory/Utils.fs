[<AutoOpen>]
module Brainloop.Memory.Utils

open Fun.Result


let (|IMAGE|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".jpg"
    | SafeStringEndWithCi ".jpeg"
    | SafeStringEndWithCi ".png" -> true
    | _ -> false

let (|AUDIO|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".mp3"
    | SafeStringEndWithCi ".wav" -> true
    | _ -> false

let (|VIDEO|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".mp4" -> true
    | _ -> false

let (|PDF|_|) (path: string) =
    match path with
    | SafeStringEndWithCi ".pdf" -> true
    | _ -> false
