namespace Brainloop.Memory

open System
open System.IO
open System.Threading.Tasks
open System.Runtime.CompilerServices
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.AspNetCore.Builder
open IcedTasks
open Fun.Result


[<Extension>]
type DocumentFileApis =

    [<Extension>]
    static member MapMemoryApis(endpoint: IEndpointRouteBuilder) =

        endpoint.MapGet(
            "/api/memory/document/{fileName}",
            Func<string, IDocumentService, ValueTask<IResult>>(fun fileName documentService -> valueTask {
                let file = Path.Combine(documentService.RootDir, fileName)
                if File.Exists file then
                    let contentType =
                        match file with
                        | SafeStringEndWithCi ".pdf" -> "application/pdf"
                        | SafeStringEndWithCi ".txt" -> "text/plain"
                        | SafeStringEndWithCi ".jpg"
                        | SafeStringEndWithCi ".jpeg" -> "image/jpeg"
                        | SafeStringEndWithCi ".png" -> "image/png"
                        | SafeStringEndWithCi ".gif" -> "image/gif"
                        | SafeStringEndWithCi ".mp3" -> "audio/mpeg"
                        | SafeStringEndWithCi ".wav" -> "audio/wav"
                        | SafeStringEndWithCi ".mp4" -> "video/mp4"
                        | _ -> "application/octet-stream"

                    return
                        match file with
                        | PDF -> Results.Bytes(File.ReadAllBytes file, contentType = contentType)
                        | _ -> Results.File(file, contentType = contentType)
                else
                    return Results.NotFound()
            })
        )
        |> ignore
