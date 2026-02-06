using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Solace.Shared.Management;

namespace Solace.Visualizer.Services;

public sealed class SolaceSempMonitorClient(HttpClient httpClient, IOptions<SolaceSempOptions> options) : ISolaceSempMonitorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly SolaceSempOptions _options = options.Value;
    private readonly object _configurationSync = new();
    private bool _isConfigured;

    public async Task<IReadOnlyList<JsonElement>> GetQueuesAsync(CancellationToken cancellationToken = default)
    {
        EnsureClientConfigured();

        var vpnName = Uri.EscapeDataString(_options.VpnName);
        return await SendAsync($"msgVpns/{vpnName}/queues?count=200", cancellationToken);
    }

    public async Task<IReadOnlyList<JsonElement>> GetQueueTxFlowsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        EnsureClientConfigured();

        var vpnName = Uri.EscapeDataString(_options.VpnName);
        var escapedQueueName = Uri.EscapeDataString(queueName);
        return await SendAsync($"msgVpns/{vpnName}/queues/{escapedQueueName}/txFlows?count=200", cancellationToken);
    }

    private async Task<IReadOnlyList<JsonElement>> SendAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var details = string.IsNullOrWhiteSpace(body) ? "SEMP request failed." : body;
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.StatusCode}: {details}");
        }

        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(JsonOptions, cancellationToken);
        if (document is null)
        {
            return [];
        }

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private void EnsureClientConfigured()
    {
        if (_isConfigured)
        {
            return;
        }

        lock (_configurationSync)
        {
            if (_isConfigured)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.BaseUrl)
                || string.IsNullOrWhiteSpace(_options.VpnName)
                || string.IsNullOrWhiteSpace(_options.Username)
                || string.IsNullOrWhiteSpace(_options.Password))
            {
                throw new InvalidOperationException(
                    "SEMP configuration is missing. Set SolaceSemp:BaseUrl, SolaceSemp:VpnName, SolaceSemp:Username, and SolaceSemp:Password in user-secrets.");
            }

            _httpClient.BaseAddress = NormalizeBaseUri(_options.BaseUrl);
            _httpClient.Timeout = TimeSpan.FromSeconds(25);

            var authToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

            _isConfigured = true;
        }
    }

    private static Uri NormalizeBaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("SEMP BaseUrl must be a valid absolute URI.");
        }

        var builder = new UriBuilder(uri)
        {
            Path = "/SEMP/v2/monitor/",
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }
}
