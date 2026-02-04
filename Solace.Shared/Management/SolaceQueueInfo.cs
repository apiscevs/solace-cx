using System.Text.Json.Serialization;

namespace Solace.Shared.Management;

public sealed record SolaceQueueInfo(
    [property: JsonPropertyName("queueName")] string QueueName,
    [property: JsonPropertyName("accessType")] string AccessType,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("permission")] string Permission,
    [property: JsonPropertyName("ingressEnabled")] bool IngressEnabled,
    [property: JsonPropertyName("egressEnabled")] bool EgressEnabled,
    [property: JsonPropertyName("partitionCount")] int PartitionCount)
{
    public bool IsPartitioned => PartitionCount > 0;
}

internal sealed class SempListResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = [];
}
