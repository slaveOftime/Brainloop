namespace Brainloop.Options

open System.ComponentModel.DataAnnotations

[<CLIMutable>]
type AppOptions = {
    [<Required>]
    [<AllowedValues("MsSqlServer", "SqlLite", "PostgreSQL")>]
    DataDbProvider: string

    [<Required>]
    DataDbConnectionString: string

    [<Required>]
    [<AllowedValues("MsSqlServer", "SqlLite", "PostgreSQL", "Qdrant")>]
    VectorDbProvider: string
    
    [<Required>]
    VectorDbConnectionString: string
}
