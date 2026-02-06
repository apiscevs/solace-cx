var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Solace_Admin>("admin")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Solace_Visualizer>("visualizer")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Solace_Publisher>("publisher")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Solace_Subscriber>("subscriber-a")
    .WithEndpoint("http", endpoint => endpoint.Port = 5238)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.Solace_Subscriber>("subscriber-b")
    .WithEndpoint("http", endpoint => endpoint.Port = 5239)
    .WithExternalHttpEndpoints();

builder.Build().Run();
