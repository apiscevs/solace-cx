using System.Text.Json.Serialization;

namespace Solace.Admin.Services;

public sealed record SempQueueInfo(
    [property: JsonPropertyName("queueName")] string QueueName,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("permission")] string Permission,
    [property: JsonPropertyName("ingressEnabled")] bool IngressEnabled,
    [property: JsonPropertyName("egressEnabled")] bool EgressEnabled,
    [property: JsonPropertyName("maxMsgSpoolUsage")] int MaxMsgSpoolUsage);

public sealed record SempQueueSubscription(
    [property: JsonPropertyName("subscriptionTopic")] string SubscriptionTopic);

internal sealed class SempListResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = [];
}
