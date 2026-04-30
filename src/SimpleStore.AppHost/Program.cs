var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin();

var storeDb = postgres.AddDatabase("storedb");

var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(storeDb)
    .WaitFor(storeDb);

builder.Build().Run();
