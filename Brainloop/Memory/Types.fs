namespace rec Brainloop.Memory

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.VectorData
open Brainloop.Db


type IMemoryService =
    abstract member VectorizeFile: fileName: string * ?loopContentId: int64 -> ValueTask<unit>
    abstract member VectorizeLoop: id: int64 * summary: string -> ValueTask<unit>
    abstract member VectorizeLoopContent: id: int64 * content: string -> ValueTask<unit>
    abstract member DeleteFile: fileName: string -> ValueTask<unit>
    abstract member DeleteLoop: id: int64 -> ValueTask<unit>
    abstract member DeleteLoopContent: id: int64 -> ValueTask<unit>

    abstract member VectorSearch:
        query: string *
        ?top: int *
        ?options: VectorSearchOptions<Dictionary<string, obj | null>> *
        ?distinguishBySource: bool *
        ?cancellationToken: CancellationToken ->
            IAsyncEnumerable<MemorySearchResultItem>

    abstract member Clear: unit -> ValueTask<unit>


type IDocumentService =
    abstract member RootDir: string
    // Return a file name with extension
    abstract member SaveFile:
        name: string * content: Stream * ?loopContentId: int64 * ?makeUnique: bool * ?cancellationToken: CancellationToken -> ValueTask<string>
    abstract member DeleteFile: fileName: string -> ValueTask<unit>
    abstract member ReadAsText: fileName: string -> ValueTask<string>

type MemoryEmbedding = {
    Source: MemoryEmbeddingSource
    // Like File path etc.
    SourceDetail: string
    Reference: MemoryEmbeddingReference voption
    CreatedAt: DateTimeOffset
    ChunkIndex: int
    // Like page number etc.
    ChunkReferenceId: string
    ChunkEmbedding: ReadOnlyMemory<float32> voption
    ChunkText: string
} with

    // With this, it will be easier to support different vector store, as different vectore store has different limitation on id type or other property types
    static member GetVectorDefinition(idType: Type, dimensions: int, distanceFunction: string) =
        VectorStoreCollectionDefinition(
            Properties = [|
                VectorStoreKeyProperty("Id", idType)
                VectorStoreDataProperty("SourceType", typeof<string>, IsIndexed = true)
                VectorStoreDataProperty("SourceId", typeof<string>, IsIndexed = true)
                VectorStoreDataProperty("SourceDetail", typeof<string>, IsIndexed = true, IsFullTextIndexed = true)
                VectorStoreDataProperty("ReferenceType", typeof<string>, IsIndexed = true)
                VectorStoreDataProperty("ReferenceId", typeof<string>, IsIndexed = true)
                VectorStoreDataProperty("CreatedAt", typeof<int64>)
                VectorStoreDataProperty("ChunkIndex", typeof<int>, IsIndexed = true)
                VectorStoreDataProperty("ChunkReferenceId", typeof<string>, IsIndexed = true)
                VectorStoreDataProperty("ChunkReferenceIndex", typeof<string>, IsIndexed = true)
                VectorStoreDataProperty("ChunkText", typeof<string>, IsFullTextIndexed = true)
                VectorStoreVectorProperty("ChunkEmbedding", typeof<ReadOnlyMemory<float32>>, dimensions, DistanceFunction = distanceFunction)
            |]
        )

    member this.ToVectorDictionary(id) =
        Dictionary(
            dict<string, obj> [
                "Id", id
                "SourceType",
                (match this.Source with
                 | MemoryEmbeddingSource.File _ -> nameof MemoryEmbeddingSource.File
                 | MemoryEmbeddingSource.Loop _ -> nameof MemoryEmbeddingSource.Loop
                 | MemoryEmbeddingSource.LoopContent _ -> nameof MemoryEmbeddingSource.LoopContent)
                "SourceId",
                (match this.Source with
                 | MemoryEmbeddingSource.File id -> id
                 | MemoryEmbeddingSource.Loop id -> id.ToString()
                 | MemoryEmbeddingSource.LoopContent id -> id.ToString())
                "SourceDetail", this.SourceDetail
                match this.Reference with
                | ValueSome(MemoryEmbeddingReference.LoopContent x) ->
                    "ReferenceType", nameof MemoryEmbeddingReference.LoopContent
                    "ReferenceId", string x
                | ValueNone -> ()
                "CreatedAt", this.CreatedAt.ToUnixTimeMilliseconds()
                "ChunkIndex", this.ChunkIndex
                "ChunkReferenceId", this.ChunkReferenceId
                "ChunkText", this.ChunkText
                match this.ChunkEmbedding with
                | ValueSome embedding -> "ChunkEmbedding", embedding
                | ValueNone -> ()
            ]
        )

    static member FromVectorDictionary(dict: Dictionary<string, obj>) = {
        Source =
            match dict["SourceType"] :?> string with
            | nameof MemoryEmbeddingSource.File -> MemoryEmbeddingSource.File(dict["SourceId"] :?> string)
            | nameof MemoryEmbeddingSource.Loop -> MemoryEmbeddingSource.Loop(dict["SourceId"] :?> string |> Int64.Parse)
            | nameof MemoryEmbeddingSource.LoopContent -> MemoryEmbeddingSource.LoopContent(dict["SourceId"] :?> string |> Int64.Parse)
            | ty -> failwith $"Unsupported source type {ty}"
        SourceDetail = dict["SourceDetail"] :?> string
        Reference =
            match dict["ReferenceType"] :?> string with
            | nameof MemoryEmbeddingReference.LoopContent ->
                match Int64.TryParse(dict["ReferenceId"] :?> string) with
                | true, x -> MemoryEmbeddingReference.LoopContent x |> ValueSome
                | _ -> ValueNone
            | _ -> ValueNone
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(dict["CreatedAt"] :?> int64)
        ChunkIndex = dict["ChunkIndex"] :?> int
        ChunkReferenceId = dict["ChunkReferenceId"] :?> string
        ChunkText = dict["ChunkText"] :?> string
        ChunkEmbedding =
            match dict.TryGetValue("ChunkEmbedding") with
            | true, (:? ReadOnlyMemory<float32> as embedding) -> ValueSome embedding
            | _ -> ValueNone
    }


[<RequireQualifiedAccess; Struct>]
type MemoryEmbeddingSource =
    | File of fileName: string
    | Loop of loopId: int64
    | LoopContent of loopContentId: int64

[<RequireQualifiedAccess>]
type MemoryEmbeddingReference = LoopContent of int64


type MemorySearchResultItem = {
    // In percentage, 0.0 to 100.0
    Score: float
    Text: string
    Result: MemorySearchResult
}

[<RequireQualifiedAccess; Struct>]
type MemorySearchResult =
    | File of file: MemorySearchFileResult
    | Loop of loop: Loop
    | LoopContent of loopContent: LoopContent

and MemorySearchFileResult = {
    FileName: string
    LoopContent: LoopContent voption
    PageNumber: int voption
}
