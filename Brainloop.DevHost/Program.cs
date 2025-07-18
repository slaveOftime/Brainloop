using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

var dbName = "brainloopdb";
var dbPassword = builder.AddParameter("db-password", "brainLoop_@2025123");

//var db = builder.AddSqlServer("SqlServer", password: dbPassword)
//    .WithDataVolume()
//    .AddDatabase("BrainloopDb");
var db = builder.AddPostgres("postgres", password: dbPassword)
    .WithDataVolume()
    .AddDatabase(dbName);

var qdrant = builder.AddQdrant("qdrant")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var brainloopServer = builder.AddProject<Projects.Brainloop>("brainloopserver")
    .WithEnvironment("AppOptions:DataDbProvider", "PostgreSQL")
    .WithEnvironment("AppOptions:DataDbConnectionString", db)
    .WithEnvironment("AppOptions:VectorDbProvider", "Qdrant")
    .WithEnvironment("AppOptions:VectorDbConnectionString", qdrant)
    .WaitFor(db)
    .WaitFor(qdrant);


builder.AddDockerComposeEnvironment("brainloop")
    .ConfigureComposeFile(compose =>
    {
        var brainLoopDocumentVolume = new Volume()
        {
            Name = "brainloop-documents",
            Type = "volume",
            ReadOnly = false,
            Target = "/app/documents",
            Source = VolumeNameGenerator.Generate(brainloopServer, "brainloop-documents"),
        };
        compose.AddVolume(new() { Name = brainLoopDocumentVolume.Source, Driver = "local" });
        compose.Services[brainloopServer.Resource.Name].AddVolume(brainLoopDocumentVolume);
    });


builder.Build().Run();
