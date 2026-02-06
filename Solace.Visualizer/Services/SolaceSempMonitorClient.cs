using System.Globalization;
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

    public async Task<IReadOnlyList<SempQueueSnapshot>> GetQueuesAsync(CancellationToken cancellationToken = default)
    {
        EnsureClientConfigured();

        var vpnName = Uri.EscapeDataString(_options.VpnName);
        return await SendQueueSnapshotAsync($"msgVpns/{vpnName}/queues?count=200", cancellationToken);
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

    private async Task<IReadOnlyList<SempQueueSnapshot>> SendQueueSnapshotAsync(string relativePath, CancellationToken cancellationToken)
    {
        var queues = await SendAsync(relativePath, cancellationToken);
        if (queues.Count == 0)
        {
            return [];
        }

        var countsPath = relativePath.Contains('?', StringComparison.Ordinal)
            ? $"{relativePath}&select=queueName,msgs.count"
            : $"{relativePath}?select=queueName,msgs.count";
        Dictionary<string, long?> queuedCounts;
        try
        {
            queuedCounts = await SendQueueCountsAsync(countsPath, cancellationToken);
        }
        catch
        {
            queuedCounts = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        }

        var snapshots = new SempQueueSnapshot[queues.Count];
        for (var index = 0; index < queues.Count; index++)
        {
            var queue = queues[index];
            var queueName = GetString(queue, "queueName");
            var queuedCount = queueName is not null && queuedCounts.TryGetValue(queueName, out var count)
                ? count
                : null;
            snapshots[index] = new SempQueueSnapshot(queue, queuedCount);
        }

        return snapshots;
    }

    private async Task<Dictionary<string, long?>> SendQueueCountsAsync(string relativePath, CancellationToken cancellationToken)
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
            return new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        }

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        }

        if (!document.RootElement.TryGetProperty("collections", out var collections)
            || collections.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        }

        var dataItems = data.EnumerateArray().ToArray();
        var collectionItems = collections.EnumerateArray().ToArray();
        var limit = Math.Min(dataItems.Length, collectionItems.Length);

        var counts = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < limit; index++)
        {
            var queueName = GetString(dataItems[index], "queueName");
            if (string.IsNullOrWhiteSpace(queueName))
            {
                continue;
            }

            counts[queueName] = GetNestedLong(collectionItems[index], "msgs", "count");
        }

        return counts;
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

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static long? GetNestedLong(JsonElement element, string parent, string child)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (!element.TryGetProperty(parent, out var parentElement) || parentElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parentElement.TryGetProperty(child, out var childElement))
        {
            return null;
        }

        if (childElement.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        if (childElement.ValueKind == JsonValueKind.String
            && long.TryParse(childElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        return null;
    }
}
