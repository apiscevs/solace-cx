using System.Text.Json;

namespace Solace.Visualizer.Services;

public sealed record SempQueueSnapshot(JsonElement Queue, long? QueuedMessageCount);
