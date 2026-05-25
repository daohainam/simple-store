var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgWeb();

var catalogDb = postgres.AddDatabase("catalogdb");
var orderDb = postgres.AddDatabase("orderdb");
var identityDb = postgres.AddDatabase("identitydb");

var web = builder.AddProject<Projects.SimpleStore_Web>("web")
    .WithReference(catalogDb)
    .WithReference(orderDb)
    .WithReference(identityDb)
    .WaitFor(catalogDb)
    .WaitFor(orderDb)
    .WaitFor(identityDb);

var admin = builder.AddProject<Projects.SimpleStore_Admin>("admin")
    .WithReference(catalogDb)
    .WithReference(orderDb)
    .WithReference(identityDb)
    .WaitFor(catalogDb)
    .WaitFor(orderDb)
    .WaitFor(identityDb);

builder.Build().Run();
