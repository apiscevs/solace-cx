namespace Solace.Visualizer.Services;

public sealed record VisualizerSnapshot(
    string QueueName,
    int PartitionCount,
    long? Depth,
    long? Inflight,
    long? Active,
    double? IngressRate,
    double? EgressRate,
    IReadOnlyList<QueuePartition> Partitions,
    IReadOnlyList<ConsumerFlow> Consumers,
    DateTimeOffset CapturedAtUtc,
    string? Warning);

public sealed record QueuePartition(
    int PartitionId,
    long? Depth,
    long? Unacked,
    double? IngressRate,
    double? EgressRate,
    string? AssignedConsumer);

public sealed record ConsumerFlow(
    string ClientName,
    string? FlowId,
    string State,
    double? MsgRate,
    IReadOnlyList<int> AssignedPartitions);

public sealed record SubscriberStat(
    string ClientName,
    string? QueueName,
    double? Rate,
    long AckedTotal,
    long AckedDelta,
    DateTimeOffset TimestampUtc);
