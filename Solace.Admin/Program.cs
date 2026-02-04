using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Solace.Admin.Components;
using Solace.Admin.Options;
using Solace.Admin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<SolaceSempOptions>()
    .BindConfiguration(SolaceSempOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<ISolaceSempClient, SolaceSempClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<SolaceSempOptions>>().Value;
    client.BaseAddress = SolaceSempClient.NormalizeBaseUri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(25);

    var authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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
