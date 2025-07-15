var builder = DistributedApplication.CreateBuilder(args);

var dbPassword = builder.AddParameter("db-password", "brainLoop_@2025123");

//var db = builder.AddSqlServer("SqlServer", password: dbPassword)
//    .WithDataVolume()
//    .AddDatabase("BrainloopDb");
var db = builder.AddPostgres("Postgres", password: dbPassword)
    .WithDataVolume()
    .AddDatabase("BrainloopDb");

var qdrant = builder.AddQdrant("Qdrant")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddProject<Projects.Brainloop>("BrainloopServer")
     .WithEnvironment("urls", "http://localhost:11436")
     .WithEnvironment("AppOptions:DataDbProvider", "PostgreSQL")
     .WithEnvironment("AppOptions:DataDbConnectionString", db)
     .WithEnvironment("AppOptions:VectorDbProvider", "Qdrant")
     .WithEnvironment("AppOptions:VectorDbConnectionString", qdrant)
     .WaitFor(db)
     .WaitFor(qdrant);

builder.Build().Run();
