using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Solace.Shared.Management;

public sealed class SolaceQueueCatalogClient(HttpClient httpClient, IOptions<SolaceSempOptions> options) : ISolaceQueueCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient;
    private readonly SolaceSempOptions _options = options.Value;
    private readonly object _configurationSync = new();
    private bool _isConfigured;

    public async Task<IReadOnlyList<SolaceQueueInfo>> GetQueuesAsync(CancellationToken cancellationToken = default)
    {
        EnsureClientConfigured();

        var vpnName = Uri.EscapeDataString(_options.VpnName);
        var response = await SendAsync<SempListResponse<SolaceQueueInfo>>($"msgVpns/{vpnName}/queues?count=200", cancellationToken);
        return response.Data;
    }

    private async Task<T> SendAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var details = string.IsNullOrWhiteSpace(body) ? "SEMP request failed." : body;
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.StatusCode}: {details}");
        }

        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException("SEMP returned an empty payload.");
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
        var normalized = baseUrl.Trim().TrimEnd('/');
        const string sempPath = "/SEMP/v2/config";

        if (!normalized.Contains(sempPath, StringComparison.OrdinalIgnoreCase))
        {
            normalized += sempPath;
        }

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return new Uri(normalized, UriKind.Absolute);
    }
}
