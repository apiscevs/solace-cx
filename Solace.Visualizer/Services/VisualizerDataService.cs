using System.Globalization;
using System.Text.Json;
using Solace.Shared.Management;

namespace Solace.Visualizer.Services;

public sealed class VisualizerDataService(
    ISolaceQueueCatalogClient queueCatalogClient,
    ISolaceSempMonitorClient monitorClient,
    SubscriberStatsStore statsStore)
{
    private static readonly string[] IngressRateFields =
    [
        "msgIngressRate",
        "ingressMsgRate",
        "ingressRate",
        "rxMsgRate",
        "averageRxMsgRate"
    ];

    private static readonly string[] EgressRateFields =
    [
        "msgEgressRate",
        "egressMsgRate",
        "egressRate",
        "deliveryRate",
        "txMsgRate",
        "averageTxMsgRate"
    ];

    private static readonly string[] MsgRateFields =
    [
        "msgRate",
        "egressMsgRate",
        "deliveryRate",
        "txMsgRate",
        "averageTxMsgRate"
    ];
    private static readonly string[] StateFields = ["operationalState", "state", "flowState", "activityState", "deliveryState"];
    private static readonly string[] ClientNameFields = ["clientName", "clientId", "clientUsername"];
    private static readonly string[] PartitionClientFields = ["partitionClientName", "clientName", "clientId"];
    private static readonly string[] PartitionParentFields = ["partitionQueueName", "parentQueueName"];
    private static readonly string[] FlowIdFields = ["flowId", "id"];
    private static readonly string[] PartitionIdFields = ["partitionId", "partitionNumber", "partition", "assignedPartitionId"];
    private static readonly string[] PartitionArrayFields = ["partitionIds", "partitions"];
    private static readonly string[] QueueNameFields = ["queueName", "endpointName", "boundToQueueName", "boundEndpointName"];
    private static readonly string[] AckCountFields = ["ackedMsgCount", "storeAndForwardAckedMsgCount"];

    private readonly ISolaceQueueCatalogClient _queueCatalogClient = queueCatalogClient;
    private readonly ISolaceSempMonitorClient _monitorClient = monitorClient;
    private readonly SubscriberStatsStore _statsStore = statsStore;
    private readonly Dictionary<string, RateSample> _consumerRateSamples = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SolaceQueueInfo>> GetPartitionedQueuesAsync(CancellationToken cancellationToken = default)
    {
        var queues = await _queueCatalogClient.GetQueuesAsync(cancellationToken);
        return queues
            .Where(queue => queue.IsPartitioned)
            .OrderBy(queue => queue.QueueName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<VisualizerSnapshot> GetSnapshotAsync(
        string queueName,
        int? knownPartitionCount,
        CancellationToken cancellationToken = default)
    {
        var queueSnapshots = await _monitorClient.GetQueuesAsync(cancellationToken);
        var flows = await _monitorClient.GetQueueTxFlowsAsync(queueName, cancellationToken);

        var parentSnapshot = queueSnapshots.FirstOrDefault(snapshot =>
            string.Equals(GetString(snapshot.Queue, "queueName"), queueName, StringComparison.OrdinalIgnoreCase));
        var parentQueue = parentSnapshot?.Queue ?? default;

        var partitionCount = knownPartitionCount
            ?? GetInt(parentQueue, "partitionCount")
            ?? 0;

        var partitionMetrics = new Dictionary<int, PartitionMetrics>();
        var partitionAssignments = new Dictionary<int, string>();
        foreach (var snapshot in queueSnapshots)
        {
            if (TryGetPartitionEntry(snapshot.Queue, queueName, snapshot.QueuedMessageCount, out var partitionId, out var owner, out var metrics))
            {
                partitionMetrics[partitionId] = metrics;
                if (!string.IsNullOrWhiteSpace(owner) && !partitionAssignments.ContainsKey(partitionId))
                {
                    partitionAssignments[partitionId] = owner;
                }
            }
        }

        var parentDepth = GetDepth(parentQueue, parentSnapshot?.QueuedMessageCount);
        if ((parentDepth is null || parentDepth == 0) && partitionMetrics.Count > 0)
        {
            var hasDepth = partitionMetrics.Values.Any(metrics => metrics.Depth is not null);
            if (hasDepth)
            {
                var summedDepth = partitionMetrics.Values.Sum(metrics => metrics.Depth ?? 0);
                if (parentDepth is null || summedDepth > 0)
                {
                    parentDepth = summedDepth;
                }
            }
        }

        var parentIngress = GetDouble(parentQueue, IngressRateFields);
        var parentEgress = GetDouble(parentQueue, EgressRateFields);

        if (partitionCount == 0 && partitionMetrics.Count > 0)
        {
            partitionCount = partitionMetrics.Keys.Max() + 1;
        }

        var consumerBuilders = new Dictionary<string, ConsumerAccumulator>(StringComparer.OrdinalIgnoreCase);
        var statsSnapshot = _statsStore.Snapshot;
        foreach (var flow in flows)
        {
            var clientName = GetString(flow, ClientNameFields) ?? "Unknown";
            var flowRate = GetDouble(flow, MsgRateFields) ?? ComputeFlowRate(clientName, flow);
            if (statsSnapshot.TryGetValue(clientName, out var stat)
                && string.Equals(stat.QueueName, queueName, StringComparison.OrdinalIgnoreCase)
                && stat.Rate is not null)
            {
                flowRate = stat.Rate;
            }
            if (!consumerBuilders.TryGetValue(clientName, out var accumulator))
            {
                accumulator = new ConsumerAccumulator(
                    clientName,
                    GetString(flow, FlowIdFields),
                    GetString(flow, StateFields) ?? "unknown",
                    flowRate);
                consumerBuilders[clientName] = accumulator;
            }
            else if (flowRate is not null)
            {
                accumulator.MsgRate = (accumulator.MsgRate ?? 0) + flowRate.Value;
            }

            foreach (var partitionId in ExtractPartitionIds(flow, queueName))
            {
                accumulator.AssignedPartitions.Add(partitionId);
            }
        }

        foreach (var assignment in partitionAssignments)
        {
            if (!consumerBuilders.TryGetValue(assignment.Value, out var accumulator))
            {
                statsSnapshot.TryGetValue(assignment.Value, out var stat);
                accumulator = new ConsumerAccumulator(
                    assignment.Value,
                    null,
                    "bound",
                    stat?.Rate);
                consumerBuilders[assignment.Value] = accumulator;
            }

            accumulator.AssignedPartitions.Add(assignment.Key);
        }

        var consumers = consumerBuilders.Values
            .Select(accumulator => new ConsumerFlow(
                ClientName: accumulator.ClientName,
                FlowId: accumulator.FlowId,
                State: accumulator.State,
                MsgRate: accumulator.MsgRate,
                AssignedPartitions: accumulator.AssignedPartitions.OrderBy(id => id).ToArray()))
            .OrderBy(consumer => consumer.ClientName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var assignmentMap = new Dictionary<int, string>(partitionCount);
        foreach (var assignment in partitionAssignments)
        {
            assignmentMap[assignment.Key] = assignment.Value;
        }

        foreach (var consumer in consumers)
        {
            foreach (var partitionId in consumer.AssignedPartitions)
            {
                if (!assignmentMap.ContainsKey(partitionId))
                {
                    assignmentMap[partitionId] = consumer.ClientName;
                }
            }
        }

        IReadOnlyList<QueuePartition> partitions;
        if (partitionCount > 0)
        {
            partitions = Enumerable.Range(0, partitionCount)
                .Select(id =>
                {
                    partitionMetrics.TryGetValue(id, out var metrics);
                    assignmentMap.TryGetValue(id, out var owner);
                    return new QueuePartition(
                        PartitionId: id,
                        Depth: metrics?.Depth,
                        IngressRate: metrics?.IngressRate,
                        EgressRate: metrics?.EgressRate,
                        AssignedConsumer: owner);
                })
                .ToArray();
        }
        else
        {
            partitions = partitionMetrics
                .OrderBy(entry => entry.Key)
                .Select(entry =>
                {
                    assignmentMap.TryGetValue(entry.Key, out var owner);
                    return new QueuePartition(
                        PartitionId: entry.Key,
                        Depth: entry.Value.Depth,
                        IngressRate: entry.Value.IngressRate,
                        EgressRate: entry.Value.EgressRate,
                        AssignedConsumer: owner);
                })
                .ToArray();
        }

        long? activeTotal = parentDepth;

        var warning = parentQueue.ValueKind == JsonValueKind.Undefined
            ? "Queue not found in SEMP monitor data."
            : null;

        return new VisualizerSnapshot(
            QueueName: queueName,
            PartitionCount: partitionCount,
            Depth: parentDepth,
            Active: activeTotal,
            IngressRate: parentIngress,
            EgressRate: parentEgress,
            Partitions: partitions,
            Consumers: consumers,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Warning: warning);
    }

    private static bool TryGetPartitionEntry(
        JsonElement queue,
        string parentQueueName,
        long? queuedMessageCount,
        out int partitionId,
        out string? assignedConsumer,
        out PartitionMetrics metrics)
    {
        partitionId = -1;
        assignedConsumer = null;
        metrics = new PartitionMetrics(null, null, null);

        if (queue.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        var parentName = GetString(queue, PartitionParentFields);
        if (string.IsNullOrWhiteSpace(parentName)
            || !string.Equals(parentName, parentQueueName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryGetInt(queue, PartitionIdFields, out partitionId))
        {
            assignedConsumer = GetString(queue, PartitionClientFields);
            metrics = new PartitionMetrics(
                Depth: GetDepth(queue, queuedMessageCount),
                IngressRate: GetDouble(queue, IngressRateFields),
                EgressRate: GetDouble(queue, EgressRateFields));
            return true;
        }

        return false;
    }

    private static IReadOnlyList<int> ExtractPartitionIds(JsonElement flow, string parentQueueName)
    {
        var ids = new HashSet<int>();

        if (TryGetInt(flow, PartitionIdFields, out var partitionId))
        {
            ids.Add(partitionId);
        }

        foreach (var name in PartitionArrayFields)
        {
            if (flow.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.TryGetInt32(out var value))
                    {
                        ids.Add(value);
                    }
                }
            }
        }

        var flowQueueName = GetString(flow, QueueNameFields);
        var extracted = ExtractPartitionIdFromQueueName(flowQueueName, parentQueueName);
        if (extracted is int inferred)
        {
            ids.Add(inferred);
        }

        return ids.OrderBy(id => id).ToArray();
    }

    private static int? ExtractPartitionIdFromQueueName(string? queueName, string parentQueueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            return null;
        }

        if (!queueName.StartsWith(parentQueueName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = queueName[parentQueueName.Length..];
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return null;
        }

        suffix = suffix.TrimStart('#', '/', '.', '-', '_', ':');
        var digits = new string(suffix.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        return TryGetInt(element, names, out var value) ? value : null;
    }

    private static bool TryGetInt(JsonElement element, string[] names, out int value)
    {
        value = 0;

        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                return false;
            }

            if (element.TryGetProperty(name, out var property))
            {
                if (property.TryGetInt32(out var parsed))
                {
                    value = parsed;
                    return true;
                }

                if (property.ValueKind == JsonValueKind.String
                    && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    value = parsed;
                    return true;
                }
            }
        }

        return false;
    }

    private static long? GetLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (element.TryGetProperty(name, out var property))
            {
                if (property.TryGetInt64(out var parsed))
                {
                    return parsed;
                }

                if (property.ValueKind == JsonValueKind.String
                    && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
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

    private static double? GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Undefined)
            {
                return null;
            }

            if (element.TryGetProperty(name, out var property))
            {
                if (property.TryGetDouble(out var parsed))
                {
                    return parsed;
                }

                if (property.ValueKind == JsonValueKind.String
                    && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static long? GetDepth(JsonElement element, long? queuedMessageCount)
    {
        return queuedMessageCount
            ?? GetNestedLong(element, "msgs", "count");
    }

    private double? ComputeFlowRate(string clientName, JsonElement flow)
    {
        var ackedCount = GetLong(flow, AckCountFields);
        if (ackedCount is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_consumerRateSamples.TryGetValue(clientName, out var sample))
        {
            var delta = ackedCount.Value - sample.AckedCount;
            var elapsed = (now - sample.Timestamp).TotalSeconds;
            if (elapsed > 0 && delta >= 0)
            {
                _consumerRateSamples[clientName] = new RateSample(ackedCount.Value, now);
                return delta / elapsed;
            }
        }

        _consumerRateSamples[clientName] = new RateSample(ackedCount.Value, now);
        return null;
    }

    private sealed class ConsumerAccumulator
    {
        public ConsumerAccumulator(string clientName, string? flowId, string state, double? msgRate)
        {
            ClientName = clientName;
            FlowId = flowId;
            State = state;
            MsgRate = msgRate;
        }

        public string ClientName { get; }
        public string? FlowId { get; }
        public string State { get; }
        public double? MsgRate { get; set; }
        public HashSet<int> AssignedPartitions { get; } = new();
    }

    private sealed record PartitionMetrics(long? Depth, double? IngressRate, double? EgressRate);
    private sealed record RateSample(long AckedCount, DateTimeOffset Timestamp);
}
