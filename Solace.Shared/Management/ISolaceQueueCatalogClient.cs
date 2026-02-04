namespace Solace.Shared.Management;

public interface ISolaceQueueCatalogClient
{
    Task<IReadOnlyList<SolaceQueueInfo>> GetQueuesAsync(CancellationToken cancellationToken = default);
}
