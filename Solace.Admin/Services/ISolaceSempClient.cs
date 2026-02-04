namespace Solace.Admin.Services;

public interface ISolaceSempClient
{
    Task<IReadOnlyList<SempQueueInfo>> GetQueuesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SempQueueSubscription>> GetQueueSubscriptionsAsync(string queueName, CancellationToken cancellationToken = default);
}
