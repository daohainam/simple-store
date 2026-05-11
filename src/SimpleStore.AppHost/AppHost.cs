var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgWeb();

var storeDb = postgres.AddDatabase("storedb");

var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(storeDb)
    .WaitFor(storeDb);

var admin = builder.AddProject<Projects.SimpleStore_Admin>("admin")
    .WithReference(storeDb)
    .WaitFor(storeDb);

builder.Build().Run();
