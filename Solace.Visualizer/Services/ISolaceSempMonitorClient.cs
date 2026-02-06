using System.Text.Json;

namespace Solace.Visualizer.Services;

public interface ISolaceSempMonitorClient
{
    Task<IReadOnlyList<JsonElement>> GetQueuesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JsonElement>> GetQueueTxFlowsAsync(string queueName, CancellationToken cancellationToken = default);
}
