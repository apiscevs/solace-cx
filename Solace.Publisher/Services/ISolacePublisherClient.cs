using Solace.Shared;
using Solace.Shared.Messaging;

namespace Solace.Publisher.Services;

public interface ISolacePublisherClient
{
    event Action? ConnectionChanged;

    SolaceOptions Options { get; }

    ConnectionSnapshot Connection { get; }

    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

    Task<bool> PublishDirectAsync(string topic, string payload, CancellationToken cancellationToken = default);

    Task<bool> PublishToQueueAsync(string queueName, string payload, string? partitionKey, CancellationToken cancellationToken = default);

    Task<bool> SimulateConnectionLossAsync(CancellationToken cancellationToken = default);
}
