using Solace.Shared;
using Solace.Shared.Management;
using Solace.Shared.Messaging;
using Solace.Subscriber.Components;
using Solace.Subscriber.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(SolaceSubscriberClient.ActivitySourceName));

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
builder.Services.AddSingleton<SolaceSubscriberClient>();
builder.Services.AddSingleton<ISolaceSubscriberClient>(sp => sp.GetRequiredService<SolaceSubscriberClient>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SolaceSubscriberClient>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
