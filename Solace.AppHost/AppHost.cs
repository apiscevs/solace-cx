var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Solace_Publisher>("publisher")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Solace_Subscriber>("subscriber")
    .WithExternalHttpEndpoints();

builder.Build().Run();
