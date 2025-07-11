namespace Brainloop.Memory

open System
open System.IO
open System.Text
open System.Threading
open Microsoft.Extensions.Logging
open UglyToad.PdfPig
open IcedTasks


type DocumentService(logger: ILogger<DocumentService>) =

    let documentDir = Path.Combine(Directory.GetCurrentDirectory(), "documents")

    do
        if not (Directory.Exists(documentDir)) then
            Directory.CreateDirectory(documentDir) |> ignore


    interface IDocumentService with

        member _.RootDir = documentDir

        member _.SaveFile(name, content, loopContentId, ?makeUnique, ?cancelationToken) = valueTask {
            let makeUnique = defaultArg makeUnique true
            let cancelationToken = defaultArg cancelationToken CancellationToken.None

            logger.LogInformation("Save file to disk {name}", name)

            let fileName =
                match loopContentId with
                | Some loopContentId -> $"LC-{loopContentId}-"
                | None -> ""
                + Path.GetFileNameWithoutExtension name
                + (if makeUnique then "-" + Guid.CreateVersion7().ToString("N") else "")
                + Path.GetExtension(name)

            let filePath = Path.Combine(documentDir, fileName)
            use fileStreamOutput = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None)
            do! content.CopyToAsync(fileStreamOutput, cancelationToken)

            return fileName
        }

        member _.DeleteFile(fileName) = valueTask {
            logger.LogInformation("Delete file {file}", fileName)

            let file = Path.Combine(documentDir, fileName)
            if File.Exists(file) then File.Delete(file)
        }

        member _.ReadAsText(fileName) = valueTask {
            logger.LogInformation("Read {file} as text", fileName)

            let file = Path.Combine(documentDir, fileName)

            match file with
            | IMAGE ->
                let bytes = File.ReadAllBytes(file)
                let sb = StringBuilder()
                return sb.Append("![image base64](").Append(Convert.ToBase64String(bytes)).AppendLine(")").ToString()

            | PDF ->
                use pdf = PdfDocument.Open(file)
                let sb = StringBuilder()
                pdf.GetPages() |> Seq.iter (fun x -> x.Text |> sb.AppendLine |> ignore)
                return sb.ToString()

            | VIDEO
            | AUDIO -> return ""

            | _ -> return! File.ReadAllTextAsync(file)
        }
