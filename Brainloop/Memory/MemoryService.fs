namespace Brainloop.Memory

open System
open System.IO
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Extensions.AI
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open Microsoft.Extensions.VectorData
open Microsoft.Extensions.Caching.Memory
open Microsoft.SemanticKernel.Data
open Microsoft.SemanticKernel.Text
open UglyToad.PdfPig
open UglyToad.PdfPig.Content
open UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter
open UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor
open IcedTasks
open FSharp.Control
open Fun.Result
open Brainloop.Options
open Brainloop.Db
open Brainloop.Model
open Brainloop.Share


type private MemoryCollection = VectorStoreCollection<obj, Dictionary<string, obj | null>>


type MemoryService
    (
        dbService: IDbService,
        modelService: IModelService,
        documentService: IDocumentService,
        getTextFromImageHandler: IGetTextFromImageHandler,
        memoryCache: IMemoryCache,
        vectoreStore: VectorStore,
        appOptions: IOptions<AppOptions>,
        logger: ILogger<MemoryService>
    ) as this =

    static let upsertLocker = new SemaphoreSlim(1, 1)

    let tokenCounter = TextChunker.TokenCounter(fun x -> modelService.CountTokens(x).Result)

    let keyType =
        match appOptions.Value.VectorDbProvider with
        | "SqlLite" -> typeof<string>
        | _ -> typeof<Guid>

    let distanceFunction =
        match appOptions.Value.VectorDbProvider with
        | "Qdrant"
        | "PostgreSQL" -> DistanceFunction.CosineSimilarity
        | "SqlLite"
        | "MsSqlServer" -> DistanceFunction.CosineDistance
        | _ -> failwithf "Unsupported vector db provider: %s" appOptions.Value.VectorDbProvider

    let createNewKey () : obj =
        match appOptions.Value.VectorDbProvider with
        | "SqlLite" -> Guid.NewGuid().ToString()
        | _ -> Guid.NewGuid()


    member _.GetSettings() = valueTask {
        try
            let! settings =
                dbService.DbContext
                    .Select<AppSettings>()
                    .Where(fun (x: AppSettings) -> x.TypeName = nameof AppSettingsType.MemorySettings)
                    .FirstAsync<AppSettings | null>()
            match settings with
            | { Type = AppSettingsType.MemorySettings settings } -> return settings
        with ex ->
            logger.LogWarning(ex, "Get settings failed")
            let! models = modelService.GetModelsWithCache()
            match models |> Seq.tryFind (fun x -> x.CanHandleEmbedding) with
            | None ->
                failwith "No embedding model is found"
                return MemorySettings.Default
            | Some model -> return { MemorySettings.Default with EmbeddingModelId = model.Id }
    }

    member _.GetMemoryCollection(dimensions: int) : ValueTask<MemoryCollection> = valueTask {
        if dimensions <= 0 then failwith "Invalid embedding dimentions"

        let contentVectorDefinition = MemoryEmbedding.GetVectorDefinition(keyType, dimensions, distanceFunction)

        let collection =
            vectoreStore.GetDynamicCollection($"{appOptions.Value.VectorCollectionName}_{dimensions}", contentVectorDefinition)

        do! collection.EnsureCollectionExistsAsync()

        return collection
    }

    member _.GetMemoryCollectionFromCache(model: Model) = valueTask {
        let memoryCollectionCacheKey = $"memory-collection-{model.Id}-{model.EmbeddingDimensions}"
        return!
            memoryCache.GetOrCreateAsync(
                memoryCollectionCacheKey,
                (fun _ -> this.GetMemoryCollection(model.EmbeddingDimensions).AsTask()),
                MemoryCacheEntryOptions(SlidingExpiration = TimeSpan.FromMinutes 10L)
            )
            |> Task.map (
                function
                | null -> failwith "Memory collection is not set"
                | x -> x
            )
    }

    member _.GetDependencies() = valueTask {
        let! settings = this.GetSettings()
        let! model = modelService.GetModelWithCache(settings.EmbeddingModelId)
        let! embeddingService = modelService.GetEmbeddingService(settings.EmbeddingModelId)
        let! collection = this.GetMemoryCollectionFromCache(model)
        return settings, embeddingService, collection
    }


    member _.UpsertMemory(collection: MemoryCollection, record: Dictionary<_, _>) = valueTask {
        do! upsertLocker.WaitAsync()
        try
            let maxRetry = 5
            let mutable retryCount = 0
            while retryCount < maxRetry do
                try
                    do! collection.UpsertAsync(record)
                    retryCount <- maxRetry
                with ex ->
                    retryCount <- retryCount + 1
                    if retryCount >= maxRetry then
                        logger.LogError(ex, "Upsert memory failed")
                        raise ex
                    else
                        logger.LogWarning("Upsert memory failed, will retry later ({count}/{maxCount}): {message}", retryCount, maxRetry, ex.Message)
                        do! Task.Delay(1000)
        finally
            upsertLocker.Release() |> ignore
    }

    member _.DeleteMemory(source: MemoryEmbeddingSource, collection: MemoryCollection) = valueTask {
        let sourceType, sourceId =
            match source with
            | MemoryEmbeddingSource.File x -> nameof MemoryEmbeddingSource.File, x
            | MemoryEmbeddingSource.Loop x -> nameof MemoryEmbeddingSource.Loop, x.ToString()
            | MemoryEmbeddingSource.LoopContent x -> nameof MemoryEmbeddingSource.LoopContent, x.ToString()

        do! upsertLocker.WaitAsync()

        try
            let! records =
                collection.GetAsync((fun record -> record["SourceType"] = sourceType && record["SourceId"] = sourceId), 5)
                |> TaskSeq.toListAsync

            for record in records do
                match record["Id"] with
                | null -> ()
                | x ->
                    do! collection.DeleteAsync(x)
                    logger.LogInformation("Memory is deleted for {type} {sourceId} chunk {index}", sourceType, sourceId, record["ChunkIndex"])

        with ex ->
            logger.LogError(ex, "Memory deleteing failed for {type} {sourceId}", sourceType, sourceId)

        upsertLocker.Release() |> ignore
    }


    member _.GetPdfPageParagraphs(pdfPage: Page, tokensPerChunk, tokensOfChunkOverlap) =
        let letters = pdfPage.Letters
        let words = NearestNeighbourWordExtractor.Instance.GetWords(letters)
        let textBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words)

        let pageText =
            textBlocks
            |> Seq.map (fun t -> t.Text.ReplaceLineEndings(" "))
            |> String.concat (Environment.NewLine + Environment.NewLine)

        TextChunker
            .SplitPlainTextParagraphs([ pageText ], tokensPerChunk, overlapTokens = tokensOfChunkOverlap, tokenCounter = tokenCounter)
            .Select(fun text index -> pdfPage.Number, index, text)


    member _.VectorizePdf
        (
            fileName: string,
            file: string,
            embeddingService: IEmbeddingService,
            collection: MemoryCollection,
            tokensPerChunk,
            tokensOfChunkOverlap,
            ?loopContentId: int64
        ) =
        valueTask {
            use pdf = PdfDocument.Open(file)
            let paragraphs = pdf.GetPages() |> Seq.collect (fun x -> this.GetPdfPageParagraphs(x, tokensPerChunk, tokensOfChunkOverlap))

            for (pageNumber, indexOnPage, text) in paragraphs do
                let! vector = embeddingService.GenerateVectorAsync(text)
                let embedding = {
                    Source = MemoryEmbeddingSource.File fileName
                    SourceDetail = ""
                    Reference =
                        match loopContentId with
                        | Some id -> MemoryEmbeddingReference.LoopContent id |> ValueSome
                        | _ -> ValueNone
                    CreatedAt = DateTimeOffset.UtcNow
                    ChunkReferenceId = string pageNumber
                    ChunkIndex = indexOnPage
                    ChunkText = text
                    ChunkEmbedding = ValueSome vector
                }
                do! this.UpsertMemory(collection, embedding.ToVectorDictionary(createNewKey ()))
                logger.LogInformation("Memory is added for {sourceId} pdf page {page} chunk {index}", fileName, pageNumber, indexOnPage)
        }

    member _.VectorizeTextFile
        (
            fileName: string,
            text: string,
            embeddingService: IEmbeddingService,
            collection: MemoryCollection,
            tokensPerChunk,
            tokensOfChunkOverlap,
            ?loopContentId: int64
        ) =
        valueTask {
            if String.IsNullOrWhiteSpace text then
                logger.LogWarning("File is empty {file}", fileName)
            else
                let chunks =
                    TextChunker.SplitPlainTextParagraphs([ text ], tokensPerChunk, overlapTokens = tokensOfChunkOverlap, tokenCounter = tokenCounter)
                for index, chunk in Seq.indexed chunks do
                    let! vector = embeddingService.GenerateVectorAsync(chunk)
                    let embedding = {
                        Source = MemoryEmbeddingSource.File fileName
                        SourceDetail = ""
                        Reference =
                            match loopContentId with
                            | Some id -> MemoryEmbeddingReference.LoopContent id |> ValueSome
                            | _ -> ValueNone
                        CreatedAt = DateTimeOffset.UtcNow
                        ChunkReferenceId = ""
                        ChunkIndex = index
                        ChunkText = chunk
                        ChunkEmbedding = ValueSome vector
                    }
                    do! this.UpsertMemory(collection, embedding.ToVectorDictionary(createNewKey ()))
                    logger.LogInformation("Memory is added for {sourceId} chunk {index}", fileName, index)
        }

    member _.FromTextSearchOptions(options: TextSearchOptions | null) =
        let top =
            match options with
            | null -> 10
            | x -> x.Top

        let vectorSearchOptions =
            match options with
            | null -> VectorSearchOptions()
            | x -> VectorSearchOptions(Skip = x.Skip)

        top, vectorSearchOptions

    member _.VectorizeFile(settings: MemorySettings, embeddingService: IEmbeddingService, collection: MemoryCollection, fileName, ?loopContentId) = valueTask {
        try
            logger.LogInformation("Vectorizing file {file} {loopContentId}", fileName, loopContentId)

            do! (this :> IMemoryService).DeleteFile(fileName)

            let file = Path.Combine(documentService.RootDir, fileName)

            match fileName with
            | IMAGE ->
                let! text = getTextFromImageHandler.Handle(file, CancellationToken.None)
                if String.IsNullOrWhiteSpace text then
                    logger.LogWarning("No text is extracted from image {file}", file)
                else
                    do!
                        this.VectorizeTextFile(
                            fileName,
                            text,
                            embeddingService,
                            collection,
                            settings.TokensOfChunk,
                            settings.TokensOfChunkOverlap,
                            ?loopContentId = loopContentId
                        )
            | AUDIO
            | VIDEO -> logger.LogWarning("Media file is ignored for vectorizing")
            | SafeStringEndWithCi ".pdf" ->
                do!
                    this.VectorizePdf(
                        fileName,
                        file,
                        embeddingService,
                        collection,
                        settings.TokensOfChunk,
                        settings.TokensOfChunkOverlap,
                        ?loopContentId = loopContentId
                    )
            | _ ->
                do!
                    this.VectorizeTextFile(
                        fileName,
                        File.ReadAllText file,
                        embeddingService,
                        collection,
                        settings.TokensOfChunk,
                        settings.TokensOfChunkOverlap,
                        ?loopContentId = loopContentId
                    )

        with ex ->
            logger.LogError(ex, "Memorize file failed")
    }

    member _.VectorizeLoop(settings: MemorySettings, embeddingService: IEmbeddingService, collection: MemoryCollection, id: int64, summary) = valueTask {
        try
            logger.LogInformation("Vectorizing loop {id}", id)

            if String.IsNullOrWhiteSpace summary then
                logger.LogWarning("Loop summary is empty for {id}", id)
            else

                do! this.DeleteMemory(MemoryEmbeddingSource.Loop id, collection)
                let chunks =
                    TextChunker.SplitPlainTextParagraphs(
                        [ summary ],
                        settings.TokensOfChunk,
                        overlapTokens = settings.TokensOfChunkOverlap,
                        tokenCounter = tokenCounter
                    )
                for index, text in Seq.indexed chunks do
                    let! vector = embeddingService.GenerateVectorAsync(text)
                    let embedding = {
                        Source = MemoryEmbeddingSource.Loop id
                        SourceDetail = ""
                        Reference = ValueNone
                        CreatedAt = DateTimeOffset.UtcNow
                        ChunkReferenceId = ""
                        ChunkIndex = index
                        ChunkText = text
                        ChunkEmbedding = ValueSome vector
                    }
                    do! this.UpsertMemory(collection, embedding.ToVectorDictionary(createNewKey ()))

                    logger.LogInformation("Memory is added for loop {sourceId} chunk {index}", id, index)

        with ex ->
            logger.LogError(ex, "Memorize loop failed")
    }

    member _.VectorizeLoopContent(settings: MemorySettings, embeddingService: IEmbeddingService, collection: MemoryCollection, id: int64, content) = valueTask {
        try
            logger.LogInformation("Vectorizing loop content {id}", id)

            if String.IsNullOrWhiteSpace content then
                logger.LogWarning("Loop content is empty for {id}", id)
            else
                do! this.DeleteMemory(MemoryEmbeddingSource.LoopContent id, collection)
                let chunks =
                    TextChunker.SplitPlainTextParagraphs(
                        [ content ],
                        settings.TokensOfChunk,
                        overlapTokens = settings.TokensOfChunkOverlap,
                        tokenCounter = tokenCounter
                    )
                for index, chunk in Seq.indexed chunks do
                    let! vector = embeddingService.GenerateVectorAsync(chunk)
                    let embedding = {
                        Source = MemoryEmbeddingSource.LoopContent id
                        SourceDetail = ""
                        Reference = ValueNone
                        CreatedAt = DateTimeOffset.UtcNow
                        ChunkReferenceId = ""
                        ChunkIndex = index
                        ChunkText = chunk
                        ChunkEmbedding = ValueSome vector
                    }
                    do! this.UpsertMemory(collection, embedding.ToVectorDictionary(createNewKey ()))

                    logger.LogInformation("Memory is added for loop content {sourceId} chunk {index}", id, index)

        with ex ->
            logger.LogError(ex, "Memorize loop content failed")
    }


    interface IMemoryService with

        member _.VectorizeFile(id, ?loopContentId) = valueTask {
            let! settings, embeddingService, collection = this.GetDependencies()
            do! this.VectorizeFile(settings, embeddingService, collection, id, ?loopContentId = loopContentId)
        }

        member _.VectorizeLoop(id, summary) = valueTask {
            let! settings, embeddingService, collection = this.GetDependencies()
            do! this.VectorizeLoop(settings, embeddingService, collection, id, summary)
        }

        member _.VectorizeLoopContent(id, content) = valueTask {
            let! settings, embeddingService, collection = this.GetDependencies()
            do! this.VectorizeLoopContent(settings, embeddingService, collection, id, content)
        }

        member _.DeleteFile(fileName) = valueTask {
            let! _, _, collection = this.GetDependencies()
            do! this.DeleteMemory(MemoryEmbeddingSource.File fileName, collection)
        }

        member _.DeleteLoop(id) = valueTask {
            let! _, _, collection = this.GetDependencies()
            do! this.DeleteMemory(MemoryEmbeddingSource.Loop id, collection)
        }

        member _.DeleteLoopContent(id) = valueTask {
            let! _, _, collection = this.GetDependencies()
            do! this.DeleteMemory(MemoryEmbeddingSource.LoopContent id, collection)
        }


        member _.VectorSearch(query, ?top, ?options, ?distinguishBySource, ?cancellationToken) = taskSeq {
            logger.LogInformation("Start to vectorize search")

            let distinguishBySource = defaultArg distinguishBySource false

            let! _, embeddingService, collection = this.GetDependencies()
            let! queryEmbedding = embeddingService.GenerateAsync(query)

            try
                let results =
                    collection.SearchAsync(queryEmbedding, defaultArg top 10, ?options = options, ?cancellationToken = cancellationToken)

                let uniqueCheckes = List()
                for result in results do
                    try
                        let score =
                            match result.Score.HasValue, distanceFunction with
                            | true, DistanceFunction.CosineSimilarity -> result.Score.Value * 100. |> int
                            | true, DistanceFunction.CosineDistance -> (float 1 - result.Score.Value) * 100. |> int
                            | true, _ -> failwithf "Unsupported distance function: %A" distanceFunction
                            | _ -> 0
                        let record = MemoryEmbedding.FromVectorDictionary(result.Record)
                        match result.Record["SourceId"] with
                        | null -> ()
                        | sourceId ->
                            match record.Source with
                            | MemoryEmbeddingSource.File fileName ->
                                let uniqueId = struct (nameof MemoryEmbeddingSource.File, fileName.GetHashCode())
                                if not distinguishBySource || uniqueCheckes.Contains uniqueId |> not then
                                    uniqueCheckes.Add(uniqueId)
                                    let pageNumber =
                                        match record.ChunkReferenceId with
                                        | INT32 x -> ValueSome x
                                        | _ -> ValueNone
                                    let fileResult = {
                                        FileName = string fileName
                                        LoopContent = ValueNone
                                        PageNumber = pageNumber
                                    }
                                    let result = {
                                        Score = score
                                        Text = record.ChunkText
                                        Result = MemorySearchResult.File fileResult
                                    }
                                    match record.Reference with
                                    | ValueNone -> result
                                    | ValueSome(MemoryEmbeddingReference.LoopContent id) ->
                                        let loopContentUniqueId = struct (nameof MemoryEmbeddingSource.LoopContent, id.GetHashCode())
                                        if not distinguishBySource || uniqueCheckes.Contains loopContentUniqueId |> not then
                                            uniqueCheckes.Add(loopContentUniqueId)
                                            let loopContent =
                                                dbService.DbContext
                                                    .Queryable<LoopContent>()
                                                    .Where(fun (x: LoopContent) -> x.Id = id)
                                                    .First<LoopContent | null>()
                                            {
                                                result with
                                                    Result =
                                                        MemorySearchResult.File {
                                                            fileResult with
                                                                LoopContent =
                                                                    match loopContent with
                                                                    | null -> ValueNone
                                                                    | x -> ValueSome x
                                                        }
                                            }

                            | MemoryEmbeddingSource.Loop id ->
                                let uniqueId = struct (nameof MemoryEmbeddingSource.Loop, id.GetHashCode())
                                if not distinguishBySource || uniqueCheckes.Contains uniqueId |> not then
                                    let loop = dbService.DbContext.Queryable<Loop>().Where(fun (x: Loop) -> x.Id = id).First<Loop | null>()
                                    match loop with
                                    | null -> logger.LogWarning("Memory loop is not found: {id}", sourceId)
                                    | x -> {
                                        Score = score
                                        Text = record.ChunkText
                                        Result = MemorySearchResult.Loop x
                                      }

                            | MemoryEmbeddingSource.LoopContent id ->
                                let uniqueId = struct (nameof MemoryEmbeddingSource.LoopContent, id.GetHashCode())
                                if not distinguishBySource || uniqueCheckes.Contains uniqueId |> not then
                                    let loopContent =
                                        dbService.DbContext
                                            .Queryable<LoopContent>()
                                            .Where(fun (x: LoopContent) -> x.Id = id)
                                            .First<LoopContent | null>()
                                    match loopContent with
                                    | null -> logger.LogWarning("Memory loop content is not found: {id}", sourceId)
                                    | x -> {
                                        Score = score
                                        Text = record.ChunkText
                                        Result = MemorySearchResult.LoopContent x
                                      }

                    with ex ->
                        logger.LogError(ex, "Process search record failed")

            with ex ->
                logger.LogError(ex, "Vectorize search failed")
        }


        member _.Clear() = valueTask {
            logger.LogInformation("Rebuilding memory")
            let! _, _, collection = this.GetDependencies()

            logger.LogInformation("Deleting memory collection")
            do! collection.EnsureCollectionDeletedAsync()

            logger.LogInformation("Creating memory collection")
            do! collection.EnsureCollectionExistsAsync()
        }


    interface ITextSearch with
        member _.GetSearchResultsAsync(query, searchOptions, cancellationToken) = task {
            let top, options = this.FromTextSearchOptions(searchOptions)
            let results = (this :> IMemoryService).VectorSearch(query, top, options, cancellationToken = cancellationToken)
            return KernelSearchResults(results.Select(fun x -> x :> obj))
        }

        member _.GetTextSearchResultsAsync(query, searchOptions, cancellationToken) = task {
            let top, options = this.FromTextSearchOptions(searchOptions)
            let results = (this :> IMemoryService).VectorSearch(query, top, options, cancellationToken = cancellationToken)

            let resultsInTextSearchResult = taskSeq {
                for item in results do
                    match item.Result with
                    | MemorySearchResult.File x ->
                        let! content = documentService.ReadAsText(x.FileName)
                        TextSearchResult(content, Name = nameof MemoryEmbeddingSource.File, Link = $"{Strings.DocumentApi}{x.FileName}")
                    | MemorySearchResult.Loop x when String.IsNullOrEmpty x.Description |> not ->
                        TextSearchResult(x.Description, Name = nameof MemoryEmbeddingSource.Loop, Link = $"/loops/{x.Id}")
                    | MemorySearchResult.Loop _ -> ()
                    | MemorySearchResult.LoopContent x when String.IsNullOrEmpty x.Content |> not ->
                        TextSearchResult(x.Content, Name = nameof MemoryEmbeddingSource.LoopContent, Link = $"/loops/{x.LoopId}/contents/{x.Id}")
                    | MemorySearchResult.LoopContent _ -> ()
            }

            return KernelSearchResults(resultsInTextSearchResult)
        }

        member _.SearchAsync(query, searchOptions, cancellationToken) : Task<KernelSearchResults<string>> = task {
            let top, options = this.FromTextSearchOptions(searchOptions)
            let results = (this :> IMemoryService).VectorSearch(query, top, options, cancellationToken = cancellationToken)

            let resultsInString = taskSeq {
                for item in results do
                    match item.Result with
                    | MemorySearchResult.File x ->
                        let! content = documentService.ReadAsText(x.FileName)
                        content
                    | MemorySearchResult.Loop x -> x.Description
                    | MemorySearchResult.LoopContent x -> x.Content
            }

            return KernelSearchResults(resultsInString)
        }
