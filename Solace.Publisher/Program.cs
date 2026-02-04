using Solace.Publisher.Components;
using Solace.Publisher.Services;
using Solace.Shared;
using Solace.Shared.Management;
using Solace.Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(SolacePublisherClient.ActivitySourceName));

builder.Services
    .AddOptions<SolaceOptions>()
    .BindConfiguration(SolaceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<SolaceSempOptions>()
    .BindConfiguration(SolaceSempOptions.SectionName);

builder.Services.AddHttpClient<ISolaceQueueCatalogClient, SolaceQueueCatalogClient>();

builder.Services.AddSingleton<MessageHistory>();
builder.Services.AddSingleton<SolacePublisherClient>();
builder.Services.AddSingleton<ISolacePublisherClient>(sp => sp.GetRequiredService<SolacePublisherClient>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SolacePublisherClient>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
