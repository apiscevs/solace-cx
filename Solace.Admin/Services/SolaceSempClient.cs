using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Solace.Admin.Options;

namespace Solace.Admin.Services;

public sealed class SolaceSempClient(HttpClient httpClient, IOptions<SolaceSempOptions> options) : ISolaceSempClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = httpClient;
    private readonly SolaceSempOptions _options = options.Value;

    public async Task<IReadOnlyList<SempQueueInfo>> GetQueuesAsync(CancellationToken cancellationToken = default)
    {
        var vpnName = Uri.EscapeDataString(_options.VpnName);
        var response = await SendAsync<SempListResponse<SempQueueInfo>>($"msgVpns/{vpnName}/queues?count=200", cancellationToken);
        return response.Data;
    }

    public async Task<IReadOnlyList<SempQueueSubscription>> GetQueueSubscriptionsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var vpnName = Uri.EscapeDataString(_options.VpnName);
        var escapedQueueName = Uri.EscapeDataString(queueName);
        var response = await SendAsync<SempListResponse<SempQueueSubscription>>(
            $"msgVpns/{vpnName}/queues/{escapedQueueName}/subscriptions?count=200",
            cancellationToken);
        return response.Data;
    }

    public static Uri NormalizeBaseUri(string baseUrl)
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

    private async Task<T> SendAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var description = await TryReadSempErrorAsync(response, cancellationToken);
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.StatusCode}: {description}");
        }

        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException("SEMP returned an empty payload.");
    }

    private static async Task<string> TryReadSempErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return "SEMP call failed and no error body was returned.";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var description = root.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("error", out var error)
                && error.TryGetProperty("description", out var descriptionNode)
                    ? descriptionNode.GetString()
                    : null;

            return string.IsNullOrWhiteSpace(description) ? body : description;
        }
        catch (JsonException)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return "Authentication failed. Verify management username/password in user secrets.";
            }

            return body;
        }
    }
}
