using Solace.Shared;
using Solace.Shared.Messaging;

namespace Solace.Subscriber.Services;

public interface ISolaceSubscriberClient
{
    event Action? ConnectionChanged;

    SolaceOptions Options { get; }

    ConnectionSnapshot Connection { get; }

    string SubscriptionTopic { get; }

    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    Task<bool> DisconnectAsync(CancellationToken cancellationToken = default);

    Task<bool> SimulateConnectionLossAsync(CancellationToken cancellationToken = default);
}
